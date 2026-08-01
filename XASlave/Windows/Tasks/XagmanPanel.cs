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
    private string xagmanActiveCharacter = string.Empty;
    private string xagmanPreferredTonyCharacter = string.Empty;
    private string xagmanActiveMeetWorld = string.Empty;
    private string xagmanActiveMeetAetheryte = string.Empty;
    private string xagmanActiveTradePartner = string.Empty;
    private string xagmanActiveTradePartnerInstanceId = string.Empty;
    private bool xagmanRunning;
    private bool xagmanTradeSafetySessionActive;
    private bool xagmanExpectedLogout;
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
    private const int XagmanCollectionFirstCoordinationProtocolRevision = 2;
    private const string XagmanGreenGcSealsName = "Green Item GC Seals";
    private const string XagmanGreenFcCreditsName = "Green Item FC Credits / Rank Progress";
    private const double XagmanCollectionBarrierWarningSeconds = 30.0;
    private const double XagmanMissingCohortPeerFailSeconds = 60.0;
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
        ("Give", XagmanItemMode.Give, XagmanItemApplicability.All),
        ("Give if Subs", XagmanItemMode.Give, XagmanItemApplicability.HasSubmarines),
        ("Give if Retainers", XagmanItemMode.Give, XagmanItemApplicability.HasRetainers),
        ("Take", XagmanItemMode.Take, XagmanItemApplicability.All),
        ("Take if Subs", XagmanItemMode.Take, XagmanItemApplicability.HasSubmarines),
        ("Take if Retainers", XagmanItemMode.Take, XagmanItemApplicability.HasRetainers),
        ("Balance", XagmanItemMode.Balance, XagmanItemApplicability.All),
        ("Balance if Subs", XagmanItemMode.Balance, XagmanItemApplicability.HasSubmarines),
        ("Balance if Retainers", XagmanItemMode.Balance, XagmanItemApplicability.HasRetainers),
        ("TopUp", XagmanItemMode.TopUp, XagmanItemApplicability.All),
        ("TopUp if Subs", XagmanItemMode.TopUp, XagmanItemApplicability.HasSubmarines),
        ("TopUp if Retainers", XagmanItemMode.TopUp, XagmanItemApplicability.HasRetainers),
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
    private void DrawXagmanTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        if (!xagmanPendingHubEndpointInitialized)
        {
            xagmanPendingHubAddress = XagmanPeerService.NormalizeHubAddress(cfg.XagmanHubAddress);
            xagmanPendingHubPort = XagmanPeerService.NormalizePort(cfg.XagmanHubPort);
            xagmanPendingHubEndpointInitialized = true;
        }
        xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort);
        ImGui.TextColored(new Vector4(0.8f, 0.6f, 1.0f, 1.0f), "Xagman");
        ImGui.TextDisabled("Cross-client FC trade coordination with Tony and Franchise Owner roles using XA Slave peer presence.");
        if (ImGui.SmallButton($"{(cfg.XagmanWarningDetailsExpanded ? "Warning Details: Expanded" : "Warning Details: Collapsed")}##xagmanWarningDetailsToggle"))
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
        ImGui.Text("Task Extras: ");
        ImGui.SameLine();
        ImGui.TextColored(dbxOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), dbxOk ? "[Dropbox]" : "[Dropbox ✗]");
        ImGui.Spacing();
        var role = cfg.XagmanRole;
        var onh = cfg.XagmanOutsideNetworkHelper;
        if (ImGui.RadioButton("Tony##xagmanRoleTony", role == XagmanRole.Tony))
        {
            cfg.XagmanRole = XagmanRole.Tony;
            InvalidateXagmanTradeCapacityForecast();
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Franchise Owner##xagmanRoleOwner", role == XagmanRole.FranchiseOwner))
        {
            cfg.XagmanRole = XagmanRole.FranchiseOwner;
            InvalidateXagmanTradeCapacityForecast();
            cfg.Save();
        }
        ImGui.Spacing();
        DrawXagmanOnhModeCheckbox(cfg);
        onh = cfg.XagmanOutsideNetworkHelper;
        ImGui.Spacing();
        if (ImGui.SmallButton($"{(cfg.XagmanRoleInstructionsExpanded ? "Setup Guide: Expanded" : "Setup Guide: Collapsed")}##xagmanSetupGuideToggle"))
        {
            cfg.XagmanRoleInstructionsExpanded = !cfg.XagmanRoleInstructionsExpanded;
            cfg.Save();
        }
        if (cfg.XagmanRoleInstructionsExpanded)
            DrawXagmanRoleInstructions(cfg.XagmanRole);
        ImGui.Spacing();
        if (!onh)
        {
            ImGui.TextDisabled($"Applied Hub: {plugin.XagmanPeers.HubEndpoint}");
            ImGui.SetNextItemWidth(Scale(140f));
            if (ImGui.InputText("Address##xagmanAddress", ref xagmanPendingHubAddress, 128))
                xagmanPendingHubAddress = xagmanPendingHubAddress.Trim();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Scale(72f));
            if (ImGui.InputInt("Port##xagmanPort", ref xagmanPendingHubPort))
                xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort);
            ImGui.SameLine();
            if (ImGui.Button("Apply##xagmanHubEndpoint"))
            {
                var appliedEndpoint = plugin.ApplyXagmanHubEndpoint(xagmanPendingHubAddress, xagmanPendingHubPort);
                xagmanPendingHubAddress = appliedEndpoint.Address;
                xagmanPendingHubPort = appliedEndpoint.Port;
            }
            ImGui.TextDisabled("Use 127.0.0.1 for same-PC clients. Use the hub PC's LAN IP/host for multi-PC setups.");
            var xagmanPeerConnectionsEnabled = plugin.XagmanPeers.IsStarted;
            ImGui.Spacing();
            var normalizedPendingHubAddress = XagmanPeerService.NormalizeHubAddress(xagmanPendingHubAddress);
            var xagmanHasPendingHubEndpointChange = xagmanPendingHubPort != cfg.XagmanHubPort
                || !normalizedPendingHubAddress.Equals(XagmanPeerService.NormalizeHubAddress(cfg.XagmanHubAddress), StringComparison.OrdinalIgnoreCase);
            if (ImGui.Button(xagmanPeerConnectionsEnabled
                    ? "Disconnect##xagmanPeerConnectionToggle"
                    : "Connect##xagmanPeerConnectionToggle"))
            {
                plugin.SetXagmanPeerConnectionsEnabled(!xagmanPeerConnectionsEnabled);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(xagmanPeerConnectionsEnabled
                    ? "Disconnect the local Xagman peer service."
                    : xagmanHasPendingHubEndpointChange
                        ? "Connect the local Xagman peer service on the currently applied address and port. Click Apply first to use the pending values shown in the fields."
                        : "Connect the local Xagman peer service.");
            }
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.XagmanPeers.LastStatus);
        }
        ImGui.Spacing();
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            if (ImGui.Button("Import from AutoRetainer##xagmanImportTony"))
                ImportXagmanTonyCharactersFromAr();
        }
        else
        {
            if (ImGui.Button("Import from AutoRetainer##xagmanImportOwner"))
                ImportXagmanFranchiseCharactersFromAr();
        }
        if (!arConfigExists) ImGui.EndDisabled();
        if (ImGui.Button("Pull XA Database Info##xagmanPullXa"))
        {
            if (!Plugin.PlayerState.IsLoaded || TryRefreshAndSaveXagmanCurrentCharacter())
            {
                PullXaDatabaseInfo();
                ClearXagmanMatchingSelectionCaches();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Plugin.PlayerState.IsLoaded
                ? "Runs XA Database Refresh + Save for the logged-in character, then reloads all saved character data into Xagman."
                : "Reloads the last saved XA Database character data into Xagman. No live character refresh is run while logged out.");
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), arImportStatus);
        }
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            DrawXagmanWorldSelector(cfg);
            DrawXagmanAetheryteSelector(cfg);
            DrawXagmanServerMatchingPicker(cfg);
            var tonyGilMinimum = Math.Max(0, cfg.XagmanTonyGilMinimum);
            ImGui.SetNextItemWidth(Scale(140f));
            if (ImGui.InputInt("Tony Gil Minimum##xagmanTonyGilMinimum", ref tonyGilMinimum))
            {
                cfg.XagmanTonyGilMinimum = Math.Max(0, tonyGilMinimum);
                cfg.Save();
            }
            var sellWhenInventoryFull = cfg.XagmanSellWhenInventoryFull;
            if (ImGui.Checkbox("Sell When Inventory Is Full##xagmanSellWhenInventoryFull", ref sellWhenInventoryFull))
            {
                cfg.XagmanSellWhenInventoryFull = sellWhenInventoryFull;
                cfg.Save();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "When Tony's inventory fills, XA can path Tony to a supported vendor and run /ays itemsell before resuming Xagman.\n" +
                    "Supported meet locations:\n" +
                    GetXagmanTonySellSupportedLocationTooltipText() + "\n" +
                    "If Tony is anywhere else, or Tony has 990,000,000 gil or more, XA uses the normal Tony full-inventory behavior: return home, relog the next Tony, or stop if no Tony remains.");
            }
            ImGui.Spacing();
            if (cfg.XagmanServerMatchingEnabled)
            {
                var configuredServers = GetXagmanConfiguredSweepServers();
                ImGui.TextDisabled(configuredServers.Count > 0 && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)
                    ? $"Meet Destination: Server Matching - {configuredServers.Count} server(s) @ {cfg.XagmanTargetAetheryte}"
                    : "Meet Destination: Server Matching - set at least 1 server world and a shared meet location");
            }
            else if (!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld))
            {
                ImGui.TextDisabled($"Meet Destination: {GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)}");
            }
            else
            {
                ImGui.TextDisabled("Meet Destination: not set");
            }
        }
        else if (onh)
        {
            // In Outside Network Helper mode each side sets its own meet spot (there is no Tony relay).
            DrawXagmanWorldSelector(cfg);
            DrawXagmanAetheryteSelector(cfg);
            ImGui.Spacing();
            ImGui.TextDisabled(!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld)
                ? $"Meet Destination: {GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)}"
                : "Meet Destination: not set");
        }
        else
        {
            var meetWorld = xagmanRunning ? xagmanActiveMeetWorld : string.Empty;
            var meetAetheryte = xagmanRunning ? xagmanActiveMeetAetheryte : string.Empty;
            if (string.IsNullOrWhiteSpace(meetWorld))
                TryGetXagmanMeetDestinationForOwner(out meetWorld, out meetAetheryte);
            ImGui.Spacing();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(meetWorld)
                ? "Meet Destination: waiting for Tony"
                : $"Meet Destination: {GetPrepLogisticsDestinationLabel(meetWorld, meetAetheryte)}");
        }
        ImGui.Spacing();
        var autoReturnToFc = cfg.XagmanAutoReturnToFc;
        if (ImGui.Checkbox("Return to FC when finished##xagmanReturnFc", ref autoReturnToFc))
        {
            cfg.XagmanAutoReturnToFc = autoReturnToFc;
            cfg.Save();
        }
        var ignoreGilInMatchSelection = cfg.XagmanIgnoreGilInMatchingSelection;
        if (ImGui.Checkbox("Ignore Gil in Select Matching Items##xagmanIgnoreGilMatch", ref ignoreGilInMatchSelection))
        {
            cfg.XagmanIgnoreGilInMatchingSelection = ignoreGilInMatchSelection;
            cfg.Save();
        }
        var refuseTradesWhenIdle = cfg.XagmanRefuseTradesWhenIdle;
        if (ImGui.Checkbox("Refuse Trades When Idle##xagmanRefuseTradesWhenIdle", ref refuseTradesWhenIdle))
        {
            cfg.XagmanRefuseTradesWhenIdle = refuseTradesWhenIdle;
            cfg.Save();
            if (!ReconcileXagmanTradeSafetyOption() && xagmanTradeSafetySessionActive)
            {
                plugin.TaskRunner.AddLog("Xagman: stopping because the updated Refuse Trades When Idle state could not be applied safely.");
                StopXagmanTask();
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = xagmanTradeSafetySessionActive
                    ? "Xagman stopped after the trade-refusal option changed; Dropbox state remains unknown and refusal is suppressed."
                    : "Xagman stopped because the updated trade-refusal option could not be applied safely.";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Adds XA Mods > Player Mods > Refuse Trade Request while a Xagman session is idle.\n" +
                "Every Xagman session suppresses refusal before receiver auto-accept, then restores your manual preference or this idle guard only after auto-accept is confirmed off.\n" +
                "The saved Refuse Trade Request checkbox remains your independent manual preference.");
        }
        if (cfg.XagmanRefuseTradesWhenIdle || xagmanTradeSafetySessionActive)
        {
            var refusalStatus = plugin.AutoRefuseTrade.XagmanOverride switch
            {
                XagmanTradeRefusalOverride.IdleDemand when plugin.AutoRefuseTrade.IsEnabled => "On (Xagman idle)",
                XagmanTradeRefusalOverride.IdleDemand => "Unavailable (refusal hooks did not arm)",
                XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression => "Suspended for Dropbox auto-accept",
                _ when xagmanTradeSafetySessionActive && plugin.AutoRefuseTrade.IsEnabled => "Manual preference active",
                _ when xagmanTradeSafetySessionActive => "Manual preference off",
                _ => "Waiting for a Xagman session",
            };
            ImGui.TextDisabled($"Trade refusal guard: {refusalStatus}");
        }
        ImGui.Spacing();
        var regionFilter = cfg.XagmanRegionFilter;
        if (DrawRegionFilterCombo("Region##xagmanRegion", ref regionFilter))
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
                $"Start Tony ({selectedTonyChars.Count})##xagmanTonyStart",
                canStartTony,
                onh ? (SysAction)StartXagmanOnhRun : StartXagmanTonyTask,
                onh
                    ? (hasGreenValueSelectors
                        ? "Green-value targets require peer-managed Xagman and cannot run through Outside Network Helper."
                        : !allRequired
                        ? "Missing required plugins. Check the plugin status above."
                        : !hasOnhMeet
                            ? "Set your meet world and location for Outside Network Helper."
                            : "Select at least one Tony character.")
                    : !allRequired
                        ? "Missing required plugins. Check the plugin status above."
                        : !localXagmanPeerConnected
                            ? "Connect the local Xagman peer service first."
                        : !hasMeetDestination
                            ? "Select a meet world and location, or enable Server Matching and set 1+ server world plus a shared location."
                            : "Select at least one Tony character.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref xagmanShowLog);

            if (!onh)
            {
                // Add peer control buttons
                ImGui.Spacing();
                if (ImGui.Button("Start All Peers##xagmanStartAllPeers"))
                {
                    StartAllXagmanPeers();
                }
                ImGui.SameLine();
                if (ImGui.Button("Stop All Peers##xagmanStopAllPeers"))
                {
                    StopAllXagmanPeers();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Start/Stop Xagman tasks on all connected peer clients");
                ImGui.SameLine();
                if (ImGui.Button("Stop All Clients and Results##xagmanStopAllClientsAndResults"))
                {
                    StopAllXagmanClientsAndResults();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop Xagman and clear saved Tony/Franchise Owner run results on this client and all connected clients. Selections, item lists, and settings are kept.");

                if (CanRotateXagmanTonyInCurrentScope())
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Rotate Tony##xagmanRotateTony"))
                        RotateXagmanTony();
                }
            }
        }
        else
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                $"Start Owners ({selectedFranchiseChars.Count})##xagmanOwnerStart",
                canStartOwners,
                onh ? (SysAction)StartXagmanOnhRun : () => StartXagmanFranchiseTask(),
                onh
                    ? (hasGreenValueSelectors
                        ? "Green-value targets require peer-managed Xagman and cannot run through Outside Network Helper."
                        : !allRequired
                        ? "Missing required plugins. Check the plugin status above."
                        : !hasOnhMeet
                            ? "Set your meet world and location for Outside Network Helper."
                            : "Select at least one Franchise Owner character.")
                    : !allRequired
                        ? "Missing required plugins. Check the plugin status above."
                        : !localXagmanPeerConnected
                            ? "Connect the local Xagman peer service first."
                            : "Select at least one Franchise Owner character.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        }
        if (xagmanRunning)
        {
            var (total, completed) = GetXagmanDisplayProgressCounts();
            var progress = total > 0 ? (float)completed / total : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {xagmanStatus} - {xagmanStatusText}");
            if (total > 0)
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{completed}/{total}");
            if (!string.IsNullOrWhiteSpace(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);
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
                DrawXagmanItemSection("Tony Search Item List", cfg.XagmanTonyItems, "xagmanTonyItems", searchOnly: true, allowGil: false);
        }
        else
        {
            DrawXagmanItemSection("Shared Item List", cfg.XagmanItems, "xagmanItems");
            if (onh && hasGreenValueSelectors)
                ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.25f, 1.0f), "Green-value targets are paused: switch off Outside Network Helper and use connected peers.");
            if (onh)
            {
                ImGui.Spacing();
                DrawXagmanOnhFriendListSection(cfg);
            }
        }
        ImGui.Spacing();
        ImGui.Checkbox("Show Queue##xagmanQueue", ref xagmanShowQueue);
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
            ImGui.Checkbox("Show Peers##xagmanPeers", ref xagmanShowPeers);
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

        ImGui.TextColored(warningColor, "WARNING: Xagman is in beta release. Do not leave it unmonitored.");
        ImGui.TextWrapped("Recommended: enable Show Log while running Xagman. For best debugging and reporting, use Plugin Operations > Verbose Task Logging so issue reports include deeper task details.");
        ImGui.TextWrapped("Recommended: enable Logout on completion, Kill Game on completion, or Enable AR Multi Mode on completion so characters are not left idle in game.");
        ImGui.Spacing();
        ImGui.TextColored(infoColor, "First-time setup");
        ImGui.TextWrapped("1. Click Import from AutoRetainer to build the initial character list. AutoRetainer can only import characters it already knows how to log into, so each character must have been logged in at least once for AutoRetainer to register it.");
        ImGui.TextWrapped("2. Click Pull XA Database Info after importing. While logged in, this first runs XA Database Refresh + Save for the current character, then reloads all saved character data so the tables, Select Matching Items, and forecasts use the latest committed snapshot. While logged out, it reloads the last saved snapshots only.");
        ImGui.TextWrapped("3. Recommended: run Monthly Relogger with the full action sweep at least once to mass-collect the latest character information before relying on Xagman matching and inventory-driven routing.");
        ImGui.Spacing();
        ImGui.TextColored(infoColor, "Required checks before running");
        ImGui.TextWrapped("1. Lifestream must have registered plot info, and Settings > General > Enter House must be enabled when teleporting to your FC house so returns land correctly.");
        ImGui.TextWrapped("2. Dropbox must be installed and available. XA Slave now owns the `/xa db ...` queue commands directly.");
    }

    private void DrawXagmanRoleInstructions(XagmanRole role)
    {
        var sectionColor = new Vector4(0.4f, 0.8f, 1.0f, 1.0f);
        ImGui.TextWrapped("Shortcut: Ctrl+click a character name in the table to send /ays relog FirstLast@World for that character.");
        if (role == XagmanRole.Tony)
        {
            ImGui.TextColored(sectionColor, "Tony Setup");
            ImGui.TextWrapped("1. Optional: create a Tony Search Item List.");
            ImGui.TextWrapped("2. Filter region if needed or use the search bar. Select characters manually, left-click Select Matching Items to select visible Tonys that hold items from the Tony Search Item List, or use Select Current Character while logged in to add that configured home-world character regardless of filters. Right-click Select Matching Items to limit suppliers to Tonys with or without registered retainers or submarines.");
            ImGui.TextWrapped("3. Select a world to meet and a meet location as the set aetheryte.");
            ImGui.TextWrapped("4. Set Tony Gil Minimum. Default: 10000.");
            ImGui.TextWrapped("5. Connect peers. Tony is then ready for Start Tony, or you can use Start All Peers / Stop All Peers when the alt clients are connected and selected.");
            ImGui.TextWrapped("6. Tony moves into position, confirms the meetup location, and then sends the green light for everyone else to start processing.");
            ImGui.TextWrapped("7. Prioritize Characters Giving Items First belongs to each Franchise Owner client. Tony ignores Tony's local saved setting and Shared Item list. An FO with no conditional policy is effectively Off even if its hidden saved value is On: all participating FOs effectively Off starts the legacy flow, all On and valid starts collection-first, and mixed or invalid clients are named and refused before a run is frozen.");
            ImGui.TextWrapped("8. Final collection-first cleanup is scoped to that frozen run. Tony waits for every FO to acknowledge the cleanup command; a missing acknowledgement keeps Tony connected in Error instead of logging out or closing the client.");
            return;
        }

        ImGui.TextColored(sectionColor, "Franchise Owner Setup");
        ImGui.TextWrapped("1. Create a list of items in Shared Item List.");
        ImGui.Indent();
        ImGui.TextWrapped("- Give: give up to the amount to Tony; 0 gives all available stock.");
        ImGui.TextWrapped("- Take: request the amount from Tony per supply pass; 0 requests all currently tradable Tony stock.");
        ImGui.TextWrapped("- Balance: give surplus or request the deficit so the owner ends at the amount. Balance 0 offloads all.");
        ImGui.TextWrapped("- TopUp: request only the deficit to the amount and leave owner surplus untouched. TopUp 0 does nothing.");
        ImGui.TextWrapped("- A plain Give, Take, Balance, or TopUp row is the fallback for every owner. The matching 'if Subs' or 'if Retainers' row overrides it for owners with registered AutoRetainer counts.");
        ImGui.TextWrapped("- If an owner has both, 'if Subs' wins over 'if Retainers'; either conditional row wins over the plain fallback. With no matching row, that item is ignored.");
        ImGui.TextWrapped("- NQ/HQ are separate only when Lumina marks that exact item as HQ-capable. Sheet-declared NQ-only items show a fixed NQ value instead of an HQ checkbox. The same valid quality may appear once for each applicability (plain, Subs, Retainers); an exact duplicate is rejected.");
        ImGui.TextWrapped("- Conditional registration is metadata only. Non-crystal quantities come only from Inventory 1-4; elemental shards, crystals, and clusters use the player's Crystals inventory. If registration cannot be established, that conditional item group is skipped safely.");
        ImGui.TextWrapped("- Example: Ceruleum Tank Give 0 plus Balance if Subs 22,650 makes owners without registered submarines give all, while submarine owners give surplus or request their deficit to 22,650.");
        ImGui.TextWrapped("- Optional Prioritize Characters Giving Items First appears only while conditional policies exist and is owned by this FO client. Its saved value is ignored while no If Subs/Retainers policy exists. Every participating FO must effectively enable it for collection-first; all effectively Off uses legacy, while mixed, invalid, or mismatched-build cohorts are refused before startup.");
        ImGui.TextWrapped("- In collection-first mode, Give/Balance-surplus runs across every participating FO client first. Receiver-only clients pause at a visible global barrier; after every expected client acknowledges collection, Tony repeats the full world/DC sweep for Take/Balance-deficit/TopUp.");
        ImGui.TextWrapped("- The forecast shows Tony stock now, projected stock after all collection, shortage now, and projected shortage after collection. The projection is conditional when Tony collection slots, owner snapshots, or Take 0 requests are indeterminate.");
        ImGui.TextWrapped("- Collection-first is not used by Outside Network Helper. Stop/cancel never advances the barrier, and every participating client must use the same build/protocol.");
        ImGui.TextWrapped("- After the Restock barrier, each FO acknowledges Tony's run-scoped cleanup before terminal FC/logout work. If any acknowledgement is missed, Tony stays connected in visible Error for explicit recovery.");
        ImGui.Unindent();
        ImGui.TextWrapped("2. Filter region if needed or use the search bar. Select characters manually, left-click Select Matching Items to select every owner that needs the Shared Item List changes, or use Select Current Character while logged in to add that configured home-world character regardless of filters. Right-click Select Matching Items to include only owners with or without registered retainers or submarines.");
        ImGui.TextWrapped("3. Click Connect.");
        ImGui.TextWrapped("4. Franchise Owner is then ready for Tony to send the all-start trigger, or you can start one client manually with Start Owners.");
    }

    private void DrawXagmanTonyTable(Configuration cfg)
    {
        var chars = cfg.XagmanTonyCharacters;
        var charInfo = cfg.ReloggerCharacterInfo;
        DrawCharacterListHeader("Tonys", $"({chars.Count} total)", "xagmanTonyAnonymize");
        var anonymizeTonyCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();
        if (ImGui.Button("Check Visible##xagmanTonyAll"))
            SelectVisibleXagmanTonyCharacters();
        ImGui.SameLine();
        if (ImGui.Button("Clear##xagmanTonyNone"))
        {
            xagmanTonySelectedIndices.Clear();
            InvalidateXagmanTradeCapacityForecast();
        }
        ImGui.SameLine();
        if (ImGui.Button("Select Matching Items##xagmanTonyMatching"))
            SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.All);
        var tonyMatchingButtonHovered = ImGui.IsItemHovered();
        if (ImGui.BeginPopupContextItem("##xagmanTonyMatchingContext"))
        {
            if (ImGui.MenuItem("Retainers Only"))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Retainers);
            if (ImGui.MenuItem("Subs Only"))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Submarines);
            ImGui.Separator();
            if (ImGui.MenuItem("Without Retainers"))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutRetainers);
            if (ImGui.MenuItem("Without Subs"))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutSubmarines);
            ImGui.EndPopup();
        }
        if (tonyMatchingButtonHovered)
            ImGui.SetTooltip("Left-click to select visible Tonys holding a Tony Search Item List item in Inventory 1-4, or an elemental shard/crystal/cluster in the player's Crystals inventory. Saddlebags and retainer stock do not count. Right-click to add a retainer/submarine registration filter. Region, Search, and Selected Only remain active.");
        if (TryGetLoggedInXagmanCurrentCharacter(out var currentTonyCharacter))
        {
            ImGui.SameLine();
            if (ImGui.Button("Select Current Character##xagmanTonyCurrent"))
                SelectXagmanCurrentCharacter(XagmanMatchSelectionTarget.Tony, currentTonyCharacter);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Add the logged-in character to the Tony selection. This uses the XA Debug home-world Name@World identity and does not change Region, Search, Selected Only, or other selected characters.");
        }
        ImGui.SameLine();
        ImGui.Checkbox("Selected Only##xagmanTonySelOnly", ref xagmanTonyShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanTonySearch", "Search Tony name or world...", ref xagmanTonySearchFilter, 128);
        ImGui.TextDisabled("Right-click the table headers or body to show or hide columns.");
        if (ImGui.BeginTable("XagmanTonyTable", 10,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody,
            ScaledVector(0f, 175f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Inventory", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            ImGui.TableSetupColumn("Treasure", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Kits", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableSetupColumn("Tanks", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            ImGui.TableSetupColumn("Retainers##xagmanTonyRetainers", ImGuiTableColumnFlags.WidthFixed, Scale(72f));
            ImGui.TableSetupColumn("Submarines##xagmanTonySubmarines", ImGuiTableColumnFlags.WidthFixed, Scale(84f));
            ImGui.TableSetupColumn("Delete##xagmanTonyDelete", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var filtered = new List<(int OrigIdx, string CharacterName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (var i = 0; i < chars.Count; i++)
            {
                var entry = chars[i];
                var charName = entry.CharacterNameWorld;
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);
                if (!IsXagmanTonyCharacterVisible(cfg, entry, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                filtered.Add((i, charName, world, regionDc, info));
            }

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
                sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var colIdx = spec.ColumnIndex;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        var cmp = colIdx switch
                        {
                            0 => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase),
                            1 => string.Compare(WorldData.GetSortKey(a.World), WorldData.GetSortKey(b.World), StringComparison.Ordinal),
                            2 => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            3 => (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0),
                            4 => (a.Info?.TreasureValue ?? 0).CompareTo(b.Info?.TreasureValue ?? 0),
                            5 => (a.Info?.MagitekRepairKits ?? 0).CompareTo(b.Info?.MagitekRepairKits ?? 0),
                            6 => (a.Info?.CeruleumTanks ?? 0).CompareTo(b.Info?.CeruleumTanks ?? 0),
                            7 => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: false).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: false)),
                            8 => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: true).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: true)),
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
                var regionDc = row.RegionDc;
                var info = row.Info;
                var displayName = GetDisplayCharacterKey(charName, anonymizeTonyCharacters);
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
                ImGui.TextDisabled(regionDc);
                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrWhiteSpace(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (info != null && info.Gil > 0)
                    ImGui.TextDisabled(info.Gil.ToString("N0", CultureInfo.InvariantCulture));
                else
                    ImGui.TextDisabled("-");
                DrawXagmanCharacterCountCell(info?.TreasureValue ?? 0);
                DrawXagmanCharacterCountCell(info?.MagitekRepairKits ?? 0);
                DrawXagmanCharacterCountCell(info?.CeruleumTanks ?? 0);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: false), "retainer");
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: true), "submarine");
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##xagmanTonyRemove{i}"))
                {
                    chars.RemoveAt(i);
                    ReindexSelectionSet(xagmanTonySelectedIndices, i);
                    InvalidateXagmanTradeCapacityForecast();
                    cfg.Save();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndTable();
        }
        ImGui.SetNextItemWidth(Scale(260f));
        var enterPressed = ImGui.InputTextWithHint("##xagmanTonyAdd", "Name Surname@World", ref xagmanTonyNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add Tony##xagmanTonyAddBtn") || enterPressed) && !string.IsNullOrWhiteSpace(xagmanTonyNewChar))
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
        DrawCharacterListHeader("Franchise Owners", $"({chars.Count} total)", "xagmanOwnerAnonymize");
        var anonymizeFranchiseCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();
        if (ImGui.Button("Check Visible##xagmanOwnerAll"))
            SelectVisibleXagmanFranchiseCharacters();
        ImGui.SameLine();
        if (ImGui.Button("Clear##xagmanOwnerNone"))
        {
            xagmanFranchiseSelectedIndices.Clear();
            InvalidateXagmanTradeCapacityForecast();
        }
        ImGui.SameLine();
        if (ImGui.Button("Select Matching Items##xagmanOwnerMatching"))
            SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.All);
        var ownerMatchingButtonHovered = ImGui.IsItemHovered();
        if (ImGui.BeginPopupContextItem("##xagmanOwnerMatchingContext"))
        {
            if (ImGui.MenuItem("Retainers Only"))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Retainers);
            if (ImGui.MenuItem("Subs Only"))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Submarines);
            ImGui.Separator();
            if (ImGui.MenuItem("Without Retainers"))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutRetainers);
            if (ImGui.MenuItem("Without Subs"))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutSubmarines);
            ImGui.EndPopup();
        }
        if (ownerMatchingButtonHovered)
            ImGui.SetTooltip("Left-click to select every owner under the active Region and Search filters that needs the configured items based on Inventory 1-4, with elemental shards/crystals/clusters read from the player's Crystals inventory. Saddlebags and retainer stock do not count. Right-click to add a retainer/submarine registration filter.");
        if (TryGetLoggedInXagmanCurrentCharacter(out var currentOwnerCharacter))
        {
            ImGui.SameLine();
            if (ImGui.Button("Select Current Character##xagmanOwnerCurrent"))
                SelectXagmanCurrentCharacter(XagmanMatchSelectionTarget.FranchiseOwner, currentOwnerCharacter);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Add the logged-in character to the Franchise Owner selection. This uses the XA Debug home-world Name@World identity and does not change Region, Search, Selected Only, or other selected characters.");
        }
        ImGui.SameLine();
        ImGui.Checkbox("Selected Only##xagmanOwnerSelOnly", ref xagmanFranchiseShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanOwnerSearch", "Search owner name or world...", ref xagmanFranchiseSearchFilter, 128);
        ImGui.SameLine();
        if (ImGui.Button("Retainers Only##xagmanOwnerRetainersOnly"))
            SelectXagmanFranchiseCharactersWithRegisteredAutoRetainerData(submarines: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh AutoRetainer data, then replace the current selection with owners under the active Region and Search filters that have registered retainers.");
        ImGui.SameLine();
        if (ImGui.Button("Subs Only##xagmanOwnerSubsOnly"))
            SelectXagmanFranchiseCharactersWithRegisteredAutoRetainerData(submarines: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh AutoRetainer data, then replace the current selection with owners under the active Region and Search filters that have registered submarines.");
        ImGui.TextDisabled("Right-click the table headers or body to show or hide columns.");
        if (ImGui.BeginTable("XagmanOwnerTable", 10,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody,
            ScaledVector(0f, 175f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Inventory", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            ImGui.TableSetupColumn("Treasure", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Kits", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableSetupColumn("Tanks", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            ImGui.TableSetupColumn("Retainers##xagmanOwnerRetainers", ImGuiTableColumnFlags.WidthFixed, Scale(72f));
            ImGui.TableSetupColumn("Submarines##xagmanOwnerSubmarines", ImGuiTableColumnFlags.WidthFixed, Scale(84f));
            ImGui.TableSetupColumn("Delete##xagmanOwnerDelete", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var filtered = new List<(int OrigIdx, string CharacterName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (var i = 0; i < chars.Count; i++)
            {
                var charName = chars[i];
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);
                if (!IsXagmanFranchiseCharacterVisible(cfg, charName, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                filtered.Add((i, charName, world, regionDc, info));
            }

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
                sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var colIdx = spec.ColumnIndex;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        var cmp = colIdx switch
                        {
                            0 => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase),
                            1 => string.Compare(WorldData.GetSortKey(a.World), WorldData.GetSortKey(b.World), StringComparison.Ordinal),
                            2 => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            3 => (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0),
                            4 => (a.Info?.TreasureValue ?? 0).CompareTo(b.Info?.TreasureValue ?? 0),
                            5 => (a.Info?.MagitekRepairKits ?? 0).CompareTo(b.Info?.MagitekRepairKits ?? 0),
                            6 => (a.Info?.CeruleumTanks ?? 0).CompareTo(b.Info?.CeruleumTanks ?? 0),
                            7 => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: false).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: false)),
                            8 => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: true).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: true)),
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
                var regionDc = row.RegionDc;
                var info = row.Info;
                var displayName = GetDisplayCharacterKey(charName, anonymizeFranchiseCharacters);
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
                ImGui.TextDisabled(regionDc);
                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrWhiteSpace(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (info != null && info.Gil > 0)
                    ImGui.TextDisabled(info.Gil.ToString("N0", CultureInfo.InvariantCulture));
                else
                    ImGui.TextDisabled("-");
                DrawXagmanCharacterCountCell(info?.TreasureValue ?? 0);
                DrawXagmanCharacterCountCell(info?.MagitekRepairKits ?? 0);
                DrawXagmanCharacterCountCell(info?.CeruleumTanks ?? 0);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: false), "retainer");
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: true), "submarine");
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##xagmanOwnerRemove{i}"))
                {
                    chars.RemoveAt(i);
                    ReindexSelectionSet(xagmanFranchiseSelectedIndices, i);
                    InvalidateXagmanTradeCapacityForecast();
                    cfg.Save();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndTable();
        }
        ImGui.SetNextItemWidth(Scale(260f));
        var enterPressed = ImGui.InputTextWithHint("##xagmanOwnerAdd", "Name Surname@World", ref xagmanFranchiseNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add Owner##xagmanOwnerAddBtn") || enterPressed) && !string.IsNullOrWhiteSpace(xagmanFranchiseNewChar))
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
            ImGui.SetTooltip(relogBlocked
                ? "Stop the active task before using Ctrl+Click relog."
                : "Ctrl+Click to relog to this character.");

        if (!ctrlClicked || relogBlocked)
            return;

        var relogTarget = characterNameWorld.Trim();
        if (!string.IsNullOrWhiteSpace(relogTarget))
            ChatHelper.SendMessage($"/ays relog {relogTarget}");
    }
    private static void DrawXagmanCharacterCountCell(int value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(value > 0 ? value.ToString("N0", CultureInfo.InvariantCulture) : "-");
    }
    private static int GetXagmanRegisteredAutoRetainerCount(ReloggerCharacterData? info, bool submarines)
    {
        if (info?.FoundInAutoRetainer != true)
            return 0;

        return Math.Max(0, submarines ? info.SubmarineCount : info.RetainerCount);
    }
    private static void DrawXagmanAutoRetainerCountCell(int value, string resourceName)
    {
        ImGui.TableNextColumn();
        if (value <= 0)
            return;

        ImGui.TextDisabled(value.ToString("N0", CultureInfo.InvariantCulture));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{value:N0} registered AutoRetainer {resourceName}{(value == 1 ? string.Empty : "s")}.");
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
            xagmanStatusText = $"Could not read AutoRetainer registration for conditional Shared Item policies ({context}).";
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
            xagmanStatusText = "Green-value targets require peer-managed Xagman and are unavailable in Outside Network Helper.";
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            return false;
        }
        if (startDirective != null)
        {
            if (startDirective.GreenValueProtocolRevision == XagmanGreenValueProtocolRevision)
                return true;
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText =
                $"Green-value start refused: Tony did not advertise protocol {XagmanGreenValueProtocolRevision}.";
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
            $"Green-value start needs a fresh Tony peer advertising protocol {XagmanGreenValueProtocolRevision}.";
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
        => IsXagmanCollectionFirstRunActive() && xagmanRunPhase == XagmanRunPhase.Collection;

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
        if (!IsXagmanCollectionFirstRunActive() || xagmanActiveRole != XagmanRole.Tony)
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
        var phaseLabel = phase == XagmanRunPhase.Collection ? "collection" : "restock";
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = missing.Count == 0
            ? $"{phaseLabel} pass complete on every FO client; preparing the next step."
            : $"Waiting at the {phaseLabel} barrier: {completed}/{xagmanExpectedFranchiseOwnerInstanceIds.Count} FO client(s) complete.";

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
                    $"{phaseLabel} barrier stopped because {unavailableMissing.Count} expected FO client(s) disconnected or left the frozen run before acknowledging.";
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
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "collection-first phase transition"))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = "Collection barrier reached, but Dropbox auto-accept could not be confirmed off before restock.";
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
            xagmanStatusText = "Collection completed, but no Tony remains selected for the restock pass.";
            PublishXagmanPresence();
            return true;
        }

        if (xagmanServerMatchingActive)
        {
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = "Restock pass: waiting to see which servers Franchise Owners need...";
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
            FinalizeXagmanCollectionFirstOwnerSelections();
            xagmanPhaseComplete = true;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = "No restock candidates on this FO client; waiting for Tony to finish the global restock barrier.";
            PublishXagmanPresence();
            return true;
        }

        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "Restock pass is starting from the first saved owner.";
        PublishXagmanPresence();
        foreach (var failedCharacter in plugin.TaskRunner.FailedCharacters)
        {
            if (!string.IsNullOrWhiteSpace(failedCharacter))
                xagmanCollectionFirstFailedCharacters.Add(failedCharacter);
        }
        var restockSteps = BuildXagmanFranchiseSteps(xagmanOwnerRunList.ToList(), 0);
        plugin.TaskRunner.Start(
            "Xagman",
            restockSteps,
            onFinished: OnXagmanFranchiseTaskFinished,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
        foreach (var failedCharacter in xagmanCollectionFirstFailedCharacters)
        {
            if (!plugin.TaskRunner.FailedCharacters.Contains(failedCharacter))
                plugin.TaskRunner.FailedCharacters.Add(failedCharacter);
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
            if (failed.Contains(owner) || xagmanSkippedCharacters.Contains(owner))
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

    private void DrawXagmanItemSection(string title, List<XagmanItemEntry> items, string id, bool searchOnly = false, bool allowGil = true)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), title);
        if (searchOnly)
            ImGui.TextDisabled("Search-only supplier list. Imported modes and quantities are ignored and the Tony table selects visible characters that hold any listed item.");
        if (ImGui.Button($"Add Item##{id}AddItem"))
            ImGui.OpenPopup($"{id}AddItemPopup");
        if (ImGui.BeginPopup($"{id}AddItemPopup"))
        {
            if (!searchOnly)
            {
                ImGui.TextDisabled("Green gear value targets");
                foreach (var selectorKind in new[]
                         {
                             XagmanItemSelectorKind.GreenItemGcSeals,
                             XagmanItemSelectorKind.GreenItemFcCreditsRankProgress,
                         })
                {
                    var selectorName = GetXagmanGreenValueSelectorName(selectorKind);
                    if (CanAddXagmanGreenValueSelector(items, selectorKind))
                    {
                        if (ImGui.Selectable(
                                $"{selectorName}##{id}{selectorKind}",
                                false,
                                ImGuiSelectableFlags.DontClosePopups))
                        {
                            AddXagmanGreenValueSelector(items, selectorKind);
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled($"{selectorName} (All, Subs, and Retainers policies already exist)");
                    }
                }
                ImGui.Separator();
            }
            ImGui.SetNextItemWidth(Scale(280f));
            var searchChanged = ImGui.InputTextWithHint($"##{id}Search", "Search items...", ref xagmanItemSearch, 128);
            if (searchChanged || !string.Equals(xagmanItemQueryCache, xagmanItemSearch, StringComparison.Ordinal))
            {
                xagmanItemQueryCache = xagmanItemSearch;
                xagmanItemResults = SearchXagmanItems(xagmanItemSearch);
                xagmanItemSearchDisplayCount = XagmanItemSearchPageSize;
            }
            ImGui.Separator();
            if (xagmanItemResults.Count == 0)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(xagmanItemSearch) ? "Type to search..." : "No results.");
            }
            else
            {
                var availableResultCount = xagmanItemResults.Count(result =>
                    CanAddXagmanItem(items, result.ItemId, false, searchOnly));
                if (availableResultCount == 0)
                {
                    ImGui.TextDisabled(searchOnly
                        ? "All matching items are already in this list."
                        : "All matching items already have All, Subs, and Retainers policies.");
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
                        if (ImGui.Button($"Show more ({Math.Min(XagmanItemSearchPageSize, remainingResultCount):N0} of {remainingResultCount:N0})##{id}ShowMoreItems"))
                            xagmanItemSearchDisplayCount += XagmanItemSearchPageSize;
                    }
                }
            }
            ImGui.EndPopup();
        }

        if (allowGil)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Gil##{id}Gil"))
                AddXagmanItem(items, 1, "Gil", false, searchOnly);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Lists##{id}Lists"))
            ImGui.OpenPopup($"{id}ListsPopup");

        ImGui.SameLine();
        if (ImGui.Button($"Import##{id}Import"))
        {
            xagmanItemImportJson = ImGui.GetClipboardText();
            var imported = TryImportXagmanItemList(title, xagmanItemImportJson, items, out var importMessage, searchOnly);
            arImportStatus = importMessage;
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            if (imported)
            {
                xagmanItemImportJson = string.Empty;
                ClearXagmanItemSearch();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Import from XA Slave JSON, Artisan JSON, or Teamcraft quantity/name clipboard text.");

        ImGui.SameLine();
        if (ImGui.Button($"Export##{id}Export"))
            ImGui.OpenPopup($"{id}ExportPopup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Export as XA Slave JSON or Teamcraft quantity/name clipboard text.");

        if (!searchOnly)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Set All##{id}SetAll"))
                ImGui.OpenPopup($"{id}SetAllPopup");
        }

        ImGui.SameLine();
        if (ImGui.Button($"Clear##{id}Clear"))
        {
            items.Clear();
            SaveXagmanSharedItemsState();
            ClearXagmanItemSearch();
        }
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), arImportStatus);
        }
        if (!searchOnly && HasXagmanConditionalItemPolicies(items))
        {
            var cfg = plugin.Configuration;
            var disableCollectionFirstToggle = xagmanRunning || cfg.XagmanOutsideNetworkHelper;
            if (disableCollectionFirstToggle)
                ImGui.BeginDisabled();
            var prioritizeGivingFirst = cfg.XagmanPrioritizeCharactersGivingItemsFirst;
            if (ImGui.Checkbox(
                    $"Prioritize Characters Giving Items First##{id}PrioritizeGivingFirst",
                    ref prioritizeGivingFirst))
            {
                cfg.XagmanPrioritizeCharactersGivingItemsFirst = prioritizeGivingFirst;
                cfg.Save();
                InvalidateXagmanTradeCapacityForecast();
            }
            if (disableCollectionFirstToggle)
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(cfg.XagmanOutsideNetworkHelper
                    ? "Collection-first coordination requires connected Xagman peers and is not used by Outside Network Helper."
                    : xagmanRunning
                        ? "This FO preference is locked while its run is active."
                        : "This Franchise Owner advertises collection-first to Tony while at least one conditional Shared Item policy exists. The saved value is ignored when the list has no If Subs/Retainers policies. Every participating FO must enable it; Tony ignores Tony-local settings and refuses mixed or invalid cohorts before startup.");
            }
        }

        DrawXagmanSavedListsPopup(title, items, id, searchOnly);
        DrawXagmanExportPopup(title, items, id, searchOnly);
        if (!searchOnly)
            DrawXagmanMassModePopup(items, id);

        var tableColumnCount = searchOnly ? 6 : 8;
        if (ImGui.BeginTable($"{id}Table", tableColumnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, ScaledVector(0f, 150f)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableSetupColumn("GC Seals/ea", ImGuiTableColumnFlags.WidthFixed, Scale(85f));
            ImGui.TableSetupColumn("FC Credits/ea", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            if (!searchOnly)
            {
                ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, Scale(160f));
                ImGui.TableSetupColumn("Amt", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            }
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                var isGreenSelector = IsXagmanGreenValueSelector(item.SelectorKind);
                ImGui.TextDisabled(isGreenSelector
                    ? "—"
                    : item.ItemId.ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled("Mixed");
                }
                else if (!CanXagmanItemBeHq(item.ItemId))
                {
                    if (item.IsHq)
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Invalid HQ");
                    else
                        ImGui.TextDisabled("NQ");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(item.IsHq
                            ? "Lumina Item.CanBeHq is false. This impossible legacy/imported HQ row is excluded from Xagman; delete it and add the item again as NQ."
                            : "Lumina Item.CanBeHq is false. This item is NQ-only, so HQ cannot be selected.");
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
                            arImportStatus = $"Xagman: {item.ItemName} already has a {GetXagmanItemPolicyLabel(item)} row for {(isHq ? "HQ" : "NQ")}.";
                            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                        }
                    }
                }
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled("Mixed");
                }
                else if (TryGetXagmanGreenItemDisplayValues(item.ItemId, item.IsHq, out var seals, out _))
                {
                    ImGui.TextUnformatted(seals.ToString("N0", CultureInfo.InvariantCulture));
                }
                else
                {
                    ImGui.TextDisabled("—");
                }
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled("Mixed");
                }
                else if (TryGetXagmanGreenItemDisplayValues(item.ItemId, item.IsHq, out _, out var fcScaled2))
                {
                    ImGui.TextUnformatted(FormatXagmanScaled2(fcScaled2));
                }
                else
                {
                    ImGui.TextDisabled("—");
                }
                if (!searchOnly)
                {
                    ImGui.TableNextColumn();
                    var policyIndex = GetXagmanItemPolicyOptionIndex(item);
                    ImGui.SetNextItemWidth(Scale(150f));
                    var policyOptions = isGreenSelector ? xagmanGreenItemPolicyOptions : xagmanItemPolicyOptions;
                    var policyLabels = isGreenSelector ? xagmanGreenItemPolicyLabels : xagmanItemPolicyLabels;
                    if (ImGui.Combo($"##{id}Mode{i}", ref policyIndex, policyLabels, policyLabels.Length))
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
                            arImportStatus = $"Xagman: {item.ItemName} already has a policy for {option.Applicability}.";
                            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                        }
                    }
                    ImGui.TableNextColumn();
                    var qty = item.Quantity;
                    ImGui.SetNextItemWidth(Scale(60f));
                    if (ImGui.InputInt($"##{id}Qty{i}", ref qty))
                    {
                        item.Quantity = Math.Max(0, qty);
                        SaveXagmanSharedItemsState();
                    }
                }
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##{id}Rm{i}"))
                {
                    items.RemoveAt(i);
                    SaveXagmanSharedItemsState();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndTable();
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
        arImportStatus = $"Xagman: copied {title} Slave JSON ({package.ListId}) to clipboard";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void ExportXagmanTeamcraftItemList(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        if (!searchOnly && HasXagmanGreenValueSelectors(items))
        {
            arImportStatus = "Xagman: Teamcraft export cannot represent green-value selectors. Use Xagman Export for a lossless list.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(10);
            return;
        }
        if (!searchOnly && HasXagmanConditionalItemPolicies(items))
        {
            arImportStatus = "Xagman: Teamcraft export cannot represent Subs/Retainers policies. Use Xagman Export for a lossless list.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(10);
            return;
        }

        var exportedItems = CloneXagmanItemsForTeamcraftExport(items, searchOnly);
        var text = BuildXagmanTeamcraftItemListText(title, exportedItems, searchOnly);
        ImGui.SetClipboardText(text);
        arImportStatus = $"Xagman: copied {title} Teamcraft text ({exportedItems.Count} item(s)) to clipboard";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void DrawXagmanExportPopup(string title, IReadOnlyList<XagmanItemEntry> items, string id, bool searchOnly = false)
    {
        if (!ImGui.BeginPopup($"{id}ExportPopup"))
            return;
        if (ImGui.Selectable($"Xagman Export##{id}XagmanExport"))
            ExportXagmanItemList(title, items, searchOnly);
        if (ImGui.Selectable($"Teamcraft Export##{id}TeamcraftExport"))
            ExportXagmanTeamcraftItemList(title, items, searchOnly);
        ImGui.EndPopup();
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

    private bool TryImportXagmanItemList(string title, string clipboardText, List<XagmanItemEntry> items, out string message, bool searchOnly = false)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            message = "Xagman: clipboard data not supported.";
            return false;
        }
        return clipboardText.TrimStart().StartsWith("{", StringComparison.Ordinal)
            ? TryImportXagmanJsonItemList(title, clipboardText, items, out message, searchOnly)
            : TryImportXagmanTeamcraftItemList(title, clipboardText, items, out message, searchOnly);
    }

    private bool TryImportXagmanJsonItemList(string title, string json, List<XagmanItemEntry> items, out string message, bool searchOnly = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                message = "Xagman: clipboard data not supported.";
                return false;
            }
            if (TryGetXagmanJsonProperty(root, "items", out var xagmanItems) && xagmanItems.ValueKind == JsonValueKind.Array)
                return TryImportXagmanSlaveItemList(title, json, items, out message, searchOnly);
            if (TryGetXagmanJsonProperty(root, "recipes", out var artisanRecipes) && artisanRecipes.ValueKind == JsonValueKind.Array)
                return TryImportXagmanArtisanItemList(title, root, items, out message, searchOnly);
        }
        catch
        {
            message = "Xagman: clipboard data not supported.";
            return false;
        }

        message = "Xagman: clipboard data not supported.";
        return false;
    }

    private bool TryImportXagmanSlaveItemList(string title, string json, List<XagmanItemEntry> items, out string message, bool searchOnly = false)
    {
        XagmanItemListPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<XagmanItemListPackage>(json, xagmanItemListJsonOptions);
        }
        catch
        {
            message = "Xagman: clipboard data not supported.";
            return false;
        }
        if (package == null || package.Items == null)
        {
            message = "Xagman: clipboard data not supported.";
            return false;
        }
        if (package.SchemaVersion > 3)
        {
            message = $"Xagman: Slave JSON schema {package.SchemaVersion} is newer than this plugin supports.";
            return false;
        }
        if (package.Items.Any(item => !Enum.IsDefined(item.SelectorKind))
            || (!searchOnly && package.Items.Any(item => !Enum.IsDefined(item.Mode) || !Enum.IsDefined(item.Applicability))))
        {
            message = "Xagman: Slave JSON contains an unsupported selector, mode, or applicability.";
            return false;
        }
        if (package.Items.Any(item => !IsValidXagmanItemEntry(item, searchOnly)))
        {
            message = "Xagman: Slave JSON contains an invalid exact item or green-value selector row.";
            return false;
        }
        if (searchOnly && package.Items.Any(item => item.SelectorKind != XagmanItemSelectorKind.ExactItem))
        {
            message = "Xagman: search-only lists cannot contain green-value selectors.";
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
                var duplicateLabel = IsXagmanGreenValueSelector(duplicatePolicy.Key.SelectorKind)
                    ? GetXagmanGreenValueSelectorName(duplicatePolicy.Key.SelectorKind)
                    : $"item {duplicatePolicy.Key.ItemId} ({(duplicatePolicy.Key.IsHq ? "HQ" : "NQ")})";
                message = $"Xagman: Slave JSON contains a duplicate policy for {duplicateLabel}, {duplicatePolicy.Key.Applicability}.";
                return false;
            }
        }
        var importedItems = CloneXagmanItems(package.Items, searchOnly);
        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        var listId = string.IsNullOrWhiteSpace(package.ListId) ? "no-list-id" : package.ListId;
        message = $"Xagman: imported {importedItems.Count} item(s) into {title} from Slave JSON {listId}";
        return true;
    }

    private bool TryImportXagmanArtisanItemList(string title, JsonElement root, List<XagmanItemEntry> items, out string message, bool searchOnly = false)
    {
        if (!TryGetXagmanJsonProperty(root, "recipes", out var recipesElement) || recipesElement.ValueKind != JsonValueKind.Array)
        {
            message = "Xagman: clipboard data not supported.";
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
            message = "Xagman: Artisan import found 0 matching item ID(s).";
            return false;
        }

        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        var listName = TryGetXagmanJsonProperty(root, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : string.Empty;
        var sourceLabel = string.IsNullOrWhiteSpace(listName)
            ? "Artisan"
            : $"Artisan '{listName}'";
        message = unresolvedIds.Count == 0
            ? $"Xagman: imported {importedItems.Count} item(s) into {title} from {sourceLabel}."
            : $"Xagman: imported {importedItems.Count} item(s) into {title} from {sourceLabel}; skipped {unresolvedIds.Count} unknown ID(s).";
        return true;
    }

    private bool TryImportXagmanTeamcraftItemList(string title, string text, List<XagmanItemEntry> items, out string message, bool searchOnly = false)
    {
        var parsedLines = ParseXagmanTeamcraftItemLines(text);
        if (parsedLines.Count == 0)
        {
            message = "Xagman: clipboard data not supported.";
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
            message = $"Xagman: Teamcraft HQ import rejected because Lumina marks these item(s) NQ-only: {string.Join(", ", unsupportedHqNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}.";
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
            message = "Xagman: Teamcraft import found 0 matching item name(s).";
            return false;
        }

        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        message = unresolvedNames.Count == 0
            ? $"Xagman: imported {importedItems.Count} item(s) into {title} from Teamcraft."
            : $"Xagman: imported {importedItems.Count} item(s) into {title} from Teamcraft; skipped {unresolvedNames.Count} unknown name(s).";
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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Queue");
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(focusTony) ? "No Tony focus selected." : $"Tony Focus: {focusTony}");
        if (ImGui.BeginTable("XagmanQueueTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, ScaledVector(0f, 100f)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(35f));
            ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Requested", ImGuiTableColumnFlags.WidthFixed, Scale(150f));
            ImGui.TableSetupColumn("Partner", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
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
                ImGui.TextDisabled(peer.QueueRequestedAtUtc == DateTime.MinValue ? "-" : peer.QueueRequestedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(peer.ActiveTradePartner) ? "-" : peer.ActiveTradePartner);
            }
            ImGui.EndTable();
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
    private void DrawXagmanProcessingLists(TaskRunner runner)
    {
        if (xagmanRunning)
        {
            var hasTonyPlan = xagmanTonyRunPlan.Count > 0;
            var hasOwnerPlan = xagmanOwnerRunPlan.Count > 0;
            if (!hasTonyPlan && !hasOwnerPlan)
                return;

            if (hasTonyPlan)
            {
                DrawXagmanProcessingList(
                    "Tony Order",
                    xagmanTonyRunPlan,
                    GetXagmanLocalTonyCompletedCharacters(),
                    xagmanActiveRole == XagmanRole.Tony ? xagmanActiveCharacter : string.Empty,
                    runner.FailedCharacters,
                    xagmanSkippedCharacters);
            }

            if (hasTonyPlan && hasOwnerPlan)
                ImGui.Spacing();

            if (hasOwnerPlan)
            {
                var etaLabel = GetXagmanOwnerEtaLabel(xagmanOwnerRunPlan, xagmanCharDurationSeconds);
                if (!string.IsNullOrWhiteSpace(etaLabel))
                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), etaLabel);
                DrawXagmanProcessingList(
                    "Franchise Owner Order",
                    xagmanOwnerRunPlan,
                    GetXagmanLocalOwnerCompletedCharacters(),
                    xagmanActiveRole == XagmanRole.FranchiseOwner ? xagmanActiveCharacter : string.Empty,
                    runner.FailedCharacters,
                    xagmanSkippedCharacters,
                    xagmanCharDurationSeconds);
            }

            return;
        }

        // Not running: keep the last completed run visible so failed (red) and skipped (purple)
        // characters remain reviewable after logout / Enable-AR completion tasks have run.
        if (!xagmanHasLastRunSnapshot)
            return;

        var hasTonySnapshot = xagmanLastRunTonyPlan.Count > 0;
        var hasOwnerSnapshot = xagmanLastRunOwnerPlan.Count > 0;
        if (!hasTonySnapshot && !hasOwnerSnapshot)
            return;

        ImGui.TextDisabled("Last Xagman run (kept until the next start):");
        if (hasTonySnapshot)
        {
            DrawXagmanProcessingList(
                "Tony Order",
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
                "Franchise Owner Order",
                xagmanLastRunOwnerPlan,
                xagmanLastRunOwnerCompleted,
                string.Empty,
                xagmanLastRunFailedCharacters,
                xagmanLastRunSkippedCharacters,
                xagmanLastRunCharDurations);
        }

        var failedCount = xagmanLastRunFailedCharacters.Count;
        var skippedCount = xagmanLastRunSkippedCharacters.Count;
        if (failedCount > 0 || skippedCount > 0)
            ImGui.TextDisabled($"{failedCount} failed (red), {skippedCount} skipped (purple) still need trading.");
        if (ImGui.Button("Clear results##xagmanClearResults"))
            ClearXagmanRunSnapshot();
    }

    private void DrawXagmanProcessingList(
        string label,
        IReadOnlyList<string> runPlan,
        int completed,
        string activeCharacter,
        IReadOnlyCollection<string> failedKeys,
        IReadOnlyCollection<string> skippedKeys,
        IReadOnlyDictionary<string, double>? durations = null)
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

        var safeCompleted = Math.Max(0, Math.Min(completed, runPlan.Count));
        ImGui.TextDisabled($"{label} ({safeCompleted}/{runPlan.Count})");
        for (var i = 0; i < runPlan.Count; i++)
        {
            var character = runPlan[i];
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
                ImGui.TextColored(new Vector4(0.72f, 0.45f, 1.0f, 1.0f), $"  [~] {i + 1}. {character}{timeSuffix}");
            else if (failed)
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  [x] {i + 1}. {character}{timeSuffix}");
            else if (isActive)
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"  [>] {i + 1}. {character}{timeSuffix}");
            else if (i < safeCompleted)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"  [v] {i + 1}. {character}{timeSuffix}");
            else
                ImGui.TextDisabled($"  [ ] {i + 1}. {character}{timeSuffix}");
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
        var activeTony = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? "Tony"
            : xagmanActiveCharacter;
        var ownerProgress = GetXagmanOwnerProgressSnapshot();
        if (ownerProgress.Total > 0)
        {
            plugin.TaskRunner.TotalItems = ownerProgress.Total;
            plugin.TaskRunner.CompletedItems = ownerProgress.Completed;
            var remainingText = $"{ownerProgress.Remaining} remaining";
            plugin.TaskRunner.CurrentItemLabel = string.IsNullOrWhiteSpace(partnerName)
                ? $"[{ownerProgress.Completed}/{ownerProgress.Total}] {activeTony} ({remainingText})"
                : $"[{ownerProgress.Completed}/{ownerProgress.Total}] {activeTony} -> {partnerName} ({remainingText})";
            return;
        }

        var total = GetXagmanLocalTonyTotalCharacters();
        if (total <= 0)
            return;
        var current = Math.Max(1, GetXagmanLocalTonyCurrentCharacterNumber());
        plugin.TaskRunner.TotalItems = total;
        plugin.TaskRunner.CompletedItems = current;
        plugin.TaskRunner.CurrentItemLabel = string.IsNullOrWhiteSpace(partnerName)
            ? $"[{current}/{total}] {activeTony}"
            : $"[{current}/{total}] {activeTony} -> {partnerName}";
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
            return "Old build";
        if (!peer.CollectionFirstRequested)
            return "Off";
        return peer.HasConditionalItemPolicies ? "On" : "Invalid";
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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Peers");
        ImGui.TextDisabled($"{peers.Count} remote peers");

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
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), $"Remaining Characters: {remainingFranchiseOwners}");
                ImGui.TextDisabled(totalFranchiseOwners > 0
                    ? $"{completedFranchiseOwners}/{totalFranchiseOwners} owner characters completed"
                    : "Waiting for Franchise Owner progress data");

                var showManualReadyToTradeButton = false;

                // Add "Ready to Trade" button for Tony when remaining characters exist
                if (showManualReadyToTradeButton && xagmanActiveRole == XagmanRole.Tony && xagmanStatus is XagmanStatus.AtMeetSpot or XagmanStatus.ReadyForQueue)
                {
                    if (ImGui.Button("Send Ready to Trade##xagmanReadyToTrade"))
                    {
                        SendReadyToTradeSignal();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Signal remaining Franchise Owners to begin trading");
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.8f, 1.0f), "All Franchise Owners Ready!");
            }
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("XagmanPeersTable", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, ScaledVector(0f, 100f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableSetupColumn("Partner", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableHeadersRow();
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
                ImGui.TextDisabled(peer.QueueNumber > 0 ? peer.QueueNumber.ToString(CultureInfo.InvariantCulture) : "-");
                ImGui.TableNextColumn();
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(peer.ActiveTradePartner) ? "-" : peer.ActiveTradePartner);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.LastSeenUtc == DateTime.MinValue ? "-" : peer.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            ImGui.EndTable();
        }
    }

    private void SendReadyToTradeSignal()
    {
        if (xagmanActiveRole != XagmanRole.Tony || xagmanStatus is not XagmanStatus.AtMeetSpot and not XagmanStatus.ReadyForQueue)
            return;

        // Update Tony status to signal ready for trading
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = "Tony ready for trading - signaling Franchise Owners.";

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
        return Plugin.Framework.Run(action);
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
                .ToList();
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
                    xagmanStatusText = "Stopped via peer command";
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
            xagmanStatusText = $"Stopped and results cleared via {source}";
        plugin.TaskRunner.AddLog($"Xagman: Stopped task and cleared saved results via {source}");
    }

    private void RecallAllXagmanPeersForFailure()
    {
        _ = RecallAllXagmanPeersForFailureAsync();
    }

    private async System.Threading.Tasks.Task RecallAllXagmanPeersForFailureAsync()
    {
        try
        {
            if (!await EnsureXagmanPeerCommandChannelAsync("recall").ConfigureAwait(false))
                return;

            var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
            if (peers.Count == 0)
            {
                await AddXagmanPeerLogAsync("Xagman: No connected peers to send recall command to").ConfigureAwait(false);
                return;
            }

            await AddXagmanPeerLogAsync($"Xagman: Sending recall command to {peers.Count} connected peers...").ConfigureAwait(false);
            if (await plugin.XagmanPeers.SendRecallTaskToAllPeersAsync().ConfigureAwait(false))
                await AddXagmanPeerLogAsync("Xagman: Recall command sent successfully").ConfigureAwait(false);
            else
                await AddXagmanPeerLogAsync($"Xagman: Failed to send recall command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send recall command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] RecallAllXagmanPeersForFailure failed");
        }
    }

    private async System.Threading.Tasks.Task<bool> CompleteAllXagmanPeersAsync()
    {
        var coordinatedRestock = IsXagmanCollectionFirstRestockPhase();
        var completionRunId = coordinatedRestock ? xagmanRunId : string.Empty;
        var completionExpectedOwners = coordinatedRestock
            ? xagmanExpectedFranchiseOwnerInstanceIds
                .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        try
        {
            if (!await EnsureXagmanPeerCommandChannelAsync("completion").ConfigureAwait(false))
                return false;

            if (!coordinatedRestock)
            {
                var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
                if (peers.Count == 0)
                {
                    await AddXagmanPeerLogAsync("Xagman: No connected peers to send completion command to").ConfigureAwait(false);
                    return false;
                }

                await AddXagmanPeerLogAsync($"Xagman: Sending completion command to {peers.Count} connected peers...").ConfigureAwait(false);
                var sent = await plugin.XagmanPeers.SendCompleteTaskToAllPeersAsync().ConfigureAwait(false);
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

            var acknowledgedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                                 XagmanRunPhase.Restock))
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
                            XagmanRunPhase.Restock,
                            XagmanCollectionFirstCoordinationProtocolRevision,
                            completionExpectedOwners)
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
            Plugin.Framework.Run(() =>
            {
                try
                {
                    // Check if this is a Franchise Owner and can start
                    if (plugin.Configuration.XagmanRole == XagmanRole.FranchiseOwner)
                    {
                        if (HasXagmanGreenValueSelectors(plugin.Configuration.XagmanItems)
                            && startDirective.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
                        {
                            plugin.TaskRunner.AddLog(
                                $"Xagman: refused peer start because Tony did not advertise green-value protocol {XagmanGreenValueProtocolRevision}. Reload every participating client from the same source.");
                            return;
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
                            var samePriorityRun = !startDirective.CollectionFirstEnabled
                                || (IsXagmanCollectionFirstRunActive()
                                    && startDirective.RunId.Equals(xagmanRunId, StringComparison.OrdinalIgnoreCase));
                            plugin.TaskRunner.AddLog(samePriorityRun
                                ? "Xagman: Received start signal from Tony."
                                : $"Xagman: ignored start for run {startDirective.RunId} because this client is already handling run {xagmanRunId}.");
                            if (!samePriorityRun)
                                return;
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
                                    xagmanFranchiseSelectedIndices.Add(i);
                                }
                                selectedFranchiseChars = GetSelectedXagmanFranchiseCharacters();
                                plugin.TaskRunner.AddLog($"Xagman: Auto-selected {allFranchiseChars.Count} Franchise Owner characters via peer command");
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
            Plugin.Framework.Run(() =>
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
                            xagmanStatusText = "Stopped via peer command";
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
            Plugin.Framework.Run(() =>
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
            Plugin.Framework.Run(() =>
            {
                try
                {
                    StartXagmanFailureRecallTask("Xagman failed because no Tony characters remained to continue the run.");
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
            Plugin.Framework.Run(() =>
            {
                try
                {
                    if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
                        return;
                    if (completionDirective.CollectionFirstEnabled)
                    {
                        var matchesFrozenRestockRun = IsXagmanCollectionFirstRestockPhase()
                            && completionDirective.CoordinationProtocolRevision == XagmanCollectionFirstCoordinationProtocolRevision
                            && completionDirective.RunPhase == XagmanRunPhase.Restock
                            && completionDirective.RunId.Equals(xagmanRunId, StringComparison.OrdinalIgnoreCase)
                            && completionDirective.ExpectedFranchiseOwnerInstanceIds.Contains(
                                plugin.InstanceId,
                                StringComparer.OrdinalIgnoreCase);
                        if (!matchesFrozenRestockRun)
                        {
                            plugin.TaskRunner.AddLog(
                                $"Xagman: ignored completion for collection-first run {completionDirective.RunId}; this client is handling {xagmanRunId} phase {xagmanRunPhase}.");
                            return;
                        }

                        xagmanCompletionDirectiveAcknowledged = true;
                        PublishXagmanPresence();
                    }
                    else if (IsXagmanCollectionFirstRunActive())
                    {
                        plugin.TaskRunner.AddLog(
                            "Xagman: ignored an unscoped legacy completion command while a collection-first run is active.");
                        return;
                    }

                    var completionReason = completionDirective.CollectionFirstEnabled
                        ? "Xagman: every expected FO client completed the global collection and restock barriers; starting owner completion cleanup."
                        : "Xagman: Tony supply is depleted across all selected Tonys; starting owner completion cleanup.";
                    StartXagmanFranchiseCompletionTask(completionReason);
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
        if (ImGui.Button(label))
            ImGui.OpenPopup("XagmanWorldPopup");
        if (!ImGui.BeginPopup("XagmanWorldPopup"))
            return;
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanWorldFilter", "Type a world name...", ref xagmanWorldFilter, 128);
        ImGui.Separator();
        if (ImGui.Selectable("Server Matching (pick 1 world per server)", cfg.XagmanServerMatchingEnabled))
        {
            cfg.XagmanServerMatchingEnabled = true;
            cfg.Save();
            xagmanWorldFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.Separator();
        foreach (var region in WorldData.RegionOrder)
        {
            var regionWorlds = WorldData.Worlds.Where(world => world.Region == region).ToList();
            if (!regionWorlds.Any(world => string.IsNullOrWhiteSpace(xagmanWorldFilter) || world.Name.Contains(xagmanWorldFilter, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!ImGui.TreeNode(region))
                continue;
            if (!WorldData.DataCenterOrder.TryGetValue(region, out var dataCenters))
            {
                ImGui.TreePop();
                continue;
            }
            foreach (var dc in dataCenters)
            {
                var dcWorlds = regionWorlds
                    .Where(world => world.DataCenter == dc)
                    .Where(world => string.IsNullOrWhiteSpace(xagmanWorldFilter) || world.Name.Contains(xagmanWorldFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(world => world.Name)
                    .ToList();
                if (dcWorlds.Count == 0)
                    continue;
                if (!ImGui.TreeNode(dc))
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
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
        ImGui.EndPopup();
    }

    // Expandable in-place picker shown when "Server Matching" is selected: one meet world per server
    // (data center), grouped Region -> Server. The shared meet location reuses XagmanTargetAetheryte.
    private void DrawXagmanServerMatchingPicker(Configuration cfg)
    {
        if (!cfg.XagmanServerMatchingEnabled)
            return;

        ImGui.Indent(Scale(8f));
        ImGui.TextDisabled("Server Matching: pick 1 meet world per server. Owners only world-travel inside their own server.");
        foreach (var region in WorldData.RegionOrder)
        {
            if (!WorldData.DataCenterOrder.TryGetValue(region, out var servers))
                continue;
            if (!ImGui.TreeNodeEx($"{region}##xagmanSm_{region}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            foreach (var dc in servers)
            {
                var current = GetXagmanServerMeetWorld(dc);
                ImGui.TextUnformatted(dc);
                ImGui.SameLine(Scale(110f));
                ImGui.SetNextItemWidth(Scale(170f));
                var comboLabel = string.IsNullOrWhiteSpace(current) ? "(none)" : current;
                if (ImGui.BeginCombo($"##xagmanSmWorld_{dc}", comboLabel))
                {
                    if (ImGui.Selectable("(none)", string.IsNullOrWhiteSpace(current)))
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

                    ImGui.EndCombo();
                }
            }

            ImGui.TreePop();
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
            ImGui.BeginDisabled();
            ImGui.Button("Select Location##xagmanAetheryteButton");
            ImGui.EndDisabled();
            return;
        }
        ImGui.SameLine();
        var locationButtonHint = cfg.XagmanServerMatchingEnabled ? "Shared Meet Location" : "Select Location";
        var label = string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)
            ? $"{locationButtonHint}##xagmanAetheryteButton"
            : $"{cfg.XagmanTargetAetheryte}##xagmanAetheryteButton";
        if (ImGui.Button(label))
            ImGui.OpenPopup("XagmanAetherytePopup");
        if (!ImGui.BeginPopup("XagmanAetherytePopup"))
            return;
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanAetheryteFilter", "Type a location...", ref xagmanAetheryteFilter, 128);
        ImGui.Separator();
        foreach (var aetheryte in GetXagmanAetheryteNames())
        {
            if (!string.IsNullOrWhiteSpace(xagmanAetheryteFilter)
                && !aetheryte.Contains(xagmanAetheryteFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            var supportedVendorLocation = IsXagmanTonySellSupportedLocation(aetheryte);
            if (supportedVendorLocation)
                ImGui.PushStyleColor(ImGuiCol.Text, XagmanTonySellSupportedLocationColor);
            var selected = ImGui.Selectable(aetheryte, string.Equals(cfg.XagmanTargetAetheryte, aetheryte, StringComparison.OrdinalIgnoreCase));
            if (supportedVendorLocation)
                ImGui.PopStyleColor();
            if (!selected)
                continue;
            cfg.XagmanTargetAetheryte = aetheryte;
            cfg.Save();
            xagmanAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            break;
        }
        ImGui.EndPopup();
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
    private void ImportXagmanTonyCharactersFromAr()
    {
        try
        {
            var arChars = plugin.ArConfigReader.ReadCharacters();
            var cfg = plugin.Configuration;
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
            cfg.Save();
            arImportStatus = $"Xagman: added {added} Tony entries from AutoRetainer";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception ex)
        {
            arImportStatus = $"Xagman Tony import failed: {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
    }
    private void ImportXagmanFranchiseCharactersFromAr()
    {
        try
        {
            var (added, total) = ImportCharactersFromArToList(plugin.Configuration.XagmanFranchiseCharacters);
            arImportStatus = $"Xagman: added {added}/{total} owner entries from AutoRetainer";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception ex)
        {
            arImportStatus = $"Xagman owner import failed: {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
    }
    private bool IsXagmanTonyCharacterVisible(Configuration cfg, XagmanTonyCharacterEntry entry, int index)
    {
        var charName = entry.CharacterNameWorld;
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
        var index = target == XagmanMatchSelectionTarget.Tony
            ? cfg.XagmanTonyCharacters.FindIndex(entry =>
                entry.CharacterNameWorld.Equals(currentCharacter, StringComparison.OrdinalIgnoreCase))
            : cfg.XagmanFranchiseCharacters.FindIndex(entry =>
                entry.Equals(currentCharacter, StringComparison.OrdinalIgnoreCase));
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony
            ? "Tony"
            : "Franchise Owner";
        if (index < 0)
        {
            arImportStatus = $"Xagman: the logged-in character cannot be selected because it is not configured in the {selectionLabel} list.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }

        var selectedIndices = target == XagmanMatchSelectionTarget.Tony
            ? xagmanTonySelectedIndices
            : xagmanFranchiseSelectedIndices;
        var added = selectedIndices.Add(index);
        if (added)
            InvalidateXagmanTradeCapacityForecast();
        arImportStatus = added
            ? $"Xagman: selected the logged-in character in the {selectionLabel} list."
            : $"Xagman: the logged-in character is already selected in the {selectionLabel} list.";
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

        var resourceName = submarines ? "submarines" : "retainers";
        var selectedCount = matchingIndices.Count;
        arImportStatus = $"Xagman: refreshed AutoRetainer and selected {selectedCount} Franchise Owner{(selectedCount == 1 ? string.Empty : "s")} with registered {resourceName} under the active filters.";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private bool TryRefreshXagmanAutoRetainerData(
        XagmanMatchSelectionTarget target,
        out Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo> arByCharacter)
    {
        arByCharacter = new Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo>(StringComparer.OrdinalIgnoreCase);
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
        try
        {
            var arCharacters = plugin.ArConfigReader.ReadCharacters();
            if (arCharacters.Count == 0)
            {
                if (target == XagmanMatchSelectionTarget.FranchiseOwner)
                {
                    xagmanOwnerPolicyRegistrationRefreshFailed = true;
                    InvalidateXagmanTradeCapacityForecast();
                }
                arImportStatus = $"Xagman: AutoRetainer data could not be read; the current {selectionLabel} selection was preserved.";
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
            arImportStatus = $"Xagman: AutoRetainer refresh failed; the current {selectionLabel} selection was preserved. {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return false;
        }
    }
    private List<XagmanTonyCharacterEntry> GetSelectedXagmanTonyCharacters()
    {
        var chars = plugin.Configuration.XagmanTonyCharacters;
        return xagmanTonySelectedIndices
            .Where(index => index >= 0 && index < chars.Count)
            .OrderBy(index => index)
            .Select(index => chars[index])
            .ToList();
    }
    private List<string> GetSelectedXagmanFranchiseCharacters()
    {
        var chars = plugin.Configuration.XagmanFranchiseCharacters;
        return xagmanFranchiseSelectedIndices
            .Where(index => index >= 0 && index < chars.Count)
            .OrderBy(index => index)
            .Select(index => chars[index])
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
            arImportStatus = scope == XagmanAutoRetainerMatchScope.All
                ? "Xagman: no Franchise Owner characters under the active Region and Search filters to match."
                : $"Xagman: refreshed AutoRetainer, but no Franchise Owner characters {scopeQualifier} are under the active Region and Search filters.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        var items = cfg.XagmanItems
            .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
            .ToList();
        if (items.Count == 0)
        {
            xagmanFranchiseSelectedIndices.Clear();
            arImportStatus = ignoreGil
                ? "Xagman: add at least one non-gil item before selecting matching Franchise Owners."
                : "Xagman: add at least one item before selecting matching Franchise Owners.";
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

        var selectionDescription = scope == XagmanAutoRetainerMatchScope.All
            ? string.Empty
            : $" {GetXagmanMatchScopeQualifier(scope)}";
        var refreshDescription = scope == XagmanAutoRetainerMatchScope.All && !hasConditionalPolicies
            ? string.Empty
            : "refreshed AutoRetainer and ";
        arImportStatus = $"Xagman: {refreshDescription}selected {selectedCount} Franchise Owner character{(selectedCount == 1 ? string.Empty : "s")}{selectionDescription} that actually need item changes.";
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
    private static string GetXagmanMatchScopeQualifier(XagmanAutoRetainerMatchScope scope)
    {
        return scope switch
        {
            XagmanAutoRetainerMatchScope.Retainers => "with registered retainers",
            XagmanAutoRetainerMatchScope.Submarines => "with registered submarines",
            XagmanAutoRetainerMatchScope.WithoutRetainers => "without registered retainers",
            XagmanAutoRetainerMatchScope.WithoutSubmarines => "without registered submarines",
            _ => string.Empty,
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
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
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
            var scopeDescription = autoRetainerScope == XagmanAutoRetainerMatchScope.All
                ? string.Empty
                : $" {GetXagmanMatchScopeQualifier(autoRetainerScope)}";
            arImportStatus = $"Xagman: no visible {selectionLabel} characters{scopeDescription} to match.";
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
            arImportStatus = ignoreGil
                ? "Xagman: add at least one non-gil item before selecting matching characters."
                : "Xagman: add at least one item before selecting matching characters.";
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
                if (!visibleMatches.Contains(chars[i].CharacterNameWorld))
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
                if (!visibleMatches.Contains(chars[i]))
                    continue;
                xagmanFranchiseSelectedIndices.Add(i);
                selectedCount++;
            }
        }
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
        var scopeDescription = autoRetainerScope == XagmanAutoRetainerMatchScope.All
            ? string.Empty
            : $" {GetXagmanMatchScopeQualifier(autoRetainerScope)}";
        var refreshDescription = autoRetainerScope == XagmanAutoRetainerMatchScope.All
            ? string.Empty
            : "refreshed AutoRetainer and ";
        InvalidateXagmanTradeCapacityForecast();
        arImportStatus = $"Xagman: {refreshDescription}selected {selectedCount} visible {selectionLabel} character{(selectedCount == 1 ? string.Empty : "s")} with matching items{scopeDescription}.";
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
            var selectionLabel = request.Target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
            arImportStatus = $"Xagman: matching visible {selectionLabel} characters... ({request.NextQueryIndex}/{request.Queries.Count})";
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
        if (!ImGui.BeginPopup($"{id}ListsPopup"))
            return;
        ImGui.SetNextItemWidth(Scale(220f));
        ImGui.InputTextWithHint($"##{id}SaveListName", "List name...", ref xagmanSavedItemListName, 128);
        ImGui.SameLine();
        if (ImGui.Button($"Save Current##{id}SaveCurrent"))
            SaveXagmanNamedItemList(title, items, searchOnly);
        ImGui.Separator();
        var savedLists = plugin.Configuration.XagmanSavedItemLists;
        if (savedLists.Count == 0)
        {
            ImGui.TextDisabled("No saved lists.");
            ImGui.EndPopup();
            return;
        }
        foreach (var saved in savedLists.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ImGui.TextUnformatted(saved.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Load##{id}Load{saved.Name}"))
                LoadXagmanNamedItemList(saved, items, searchOnly);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
            if (ImGui.SmallButton($"X##{id}Delete{saved.Name}"))
            {
                plugin.Configuration.XagmanSavedItemLists.RemoveAll(entry => entry.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                plugin.Configuration.Save();
                ImGui.PopStyleColor();
                break;
            }
            ImGui.PopStyleColor();
        }
        ImGui.EndPopup();
    }

    private void DrawXagmanMassModePopup(List<XagmanItemEntry> items, string id)
    {
        if (!ImGui.BeginPopup($"{id}SetAllPopup"))
            return;
        if (ImGui.Selectable("Give", false))
            SetAllXagmanItemModes(items, XagmanItemMode.Give);
        if (ImGui.Selectable("Take", false))
            SetAllXagmanItemModes(items, XagmanItemMode.Take);
        if (ImGui.Selectable("Balance", false))
            SetAllXagmanItemModes(items, XagmanItemMode.Balance);
        if (ImGui.Selectable("TopUp", false))
            SetAllXagmanItemModes(items, XagmanItemMode.TopUp);
        ImGui.EndPopup();
    }

    private void SaveXagmanNamedItemList(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        var name = xagmanSavedItemListName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            arImportStatus = $"Xagman: enter a name before saving {title}.";
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
        arImportStatus = $"Xagman: saved list '{name}'.";
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
        arImportStatus = $"Xagman: loaded list '{saved.Name}'.";
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

    private bool TryRefreshAndSaveXagmanCurrentCharacter()
    {
        var expectedContentId = Plugin.PlayerState.ContentId;
        if (expectedContentId == 0)
            return SetXagmanXaDatabaseRefreshFailure("The logged-in character content ID is not ready.");

        if (!plugin.IpcClient.IsReady())
            return SetXagmanXaDatabaseRefreshFailure("XA Database is unavailable or is not ready for the logged-in character.");

        if (!plugin.IpcClient.Save())
            return SetXagmanXaDatabaseRefreshFailure("XA Database did not accept the Refresh + Save request.");

        var resultJson = plugin.IpcClient.GetLastSnapshotResultJson();
        if (string.IsNullOrWhiteSpace(resultJson))
            return SetXagmanXaDatabaseRefreshFailure("XA Database did not return a snapshot result. Update XA Database and try again.");

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Success", out var successElement)
                || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("Pending", out var pendingElement)
                || pendingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return SetXagmanXaDatabaseRefreshFailure("XA Database returned an invalid snapshot result.");
            }

            var resultSummary = root.TryGetProperty("Summary", out var summaryElement)
                && summaryElement.ValueKind == JsonValueKind.String
                    ? summaryElement.GetString() ?? string.Empty
                    : string.Empty;

            if (pendingElement.GetBoolean())
                return SetXagmanXaDatabaseRefreshFailure("XA Database reported that the snapshot is still pending.");

            if (!successElement.GetBoolean())
                return SetXagmanXaDatabaseRefreshFailure(string.IsNullOrWhiteSpace(resultSummary)
                    ? "XA Database skipped or failed the snapshot save."
                    : resultSummary);

            if (!root.TryGetProperty("ContentId", out var contentIdElement)
                || contentIdElement.ValueKind != JsonValueKind.Number
                || !contentIdElement.TryGetUInt64(out var savedContentId)
                || savedContentId != expectedContentId)
            {
                return SetXagmanXaDatabaseRefreshFailure("XA Database saved a different character than the one currently logged in.");
            }

            plugin.SlaveDatabase.RecordLastSyncedToXaDb(
                expectedContentId,
                Plugin.PlayerState.CharacterName.ToString(),
                DateTime.UtcNow);
            Plugin.Log.Information(
                $"[XASlave] Xagman confirmed XA Database Refresh + Save for content ID {expectedContentId}; pulling committed character rows.");
            return true;
        }
        catch (JsonException ex)
        {
            return SetXagmanXaDatabaseRefreshFailure($"XA Database returned an unreadable snapshot result: {ex.Message}");
        }
    }

    private bool SetXagmanXaDatabaseRefreshFailure(string detail)
    {
        arImportStatus = $"XA DB refresh + save failed: {detail}";
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
        PinXagmanMeetRouteSnapshot(serverMatching);
        if (serverMatching)
        {
            if (!TryValidateXagmanServerMatchingTravelPlan(selected, out var routeFailure))
            {
                ReportXagmanTravelRouteError($"Server Matching start rejected: {routeFailure}");
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
                if (TryValidateXagmanMeetTravel(
                        entry.CharacterNameWorld,
                        GetXagmanFixedMeetWorld(),
                        out var routeFailure))
                {
                    continue;
                }

                ReportXagmanTravelRouteError(
                    $"Tony start rejected for {entry.CharacterNameWorld}: {routeFailure}");
                ResetXagmanMeetRouteSnapshot();
                return;
            }
        }
        ClearXagmanRunSnapshot();
        ClearXagmanOwnerPolicyRunCapabilities();
        ResetXagmanFiniteTakeGoals();
        ResetXagmanCollectionFirstRunState();
        ResetXagmanServerMatchingRunState();
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearLog();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession("Tony start"))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = xagmanTradeSafetySessionActive
                    ? "Could not establish safe Dropbox/refusal coordination; Dropbox auto-accept state is unknown and refusal remains suppressed."
                    : "Dropbox auto-accept is confirmed off, but the requested manual/idle refusal state could not be established.";
            }
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            ResetXagmanMeetRouteSnapshot();
            return;
        }
        SetXagmanRunning(true);
        xagmanActiveRole = XagmanRole.Tony;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "Standing by for Franchise Owner meetup relay.";
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
            xagmanStatusText = "Server Matching: waiting to see which servers Franchise Owners need...";
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
            ClearXagmanRunSnapshot();
            ClearXagmanOwnerPolicyRunCapabilities();
            ResetXagmanFiniteTakeGoals();
            ResetXagmanServerMatchingRunState();
            ResetXagmanOwnerTimings();
            if (!TryValidateXagmanGreenValueOwnerStart(startDirective))
                return false;
            if (!TryConfigureXagmanCollectionFirstOwnerRun(startDirective))
                return false;
            // When a Server Matching Tony is already advertising, process owners in Region -> Server
            // sweep order so each character waits the minimum time for its server's turn.
            if (HasXagmanServerMatchingMeetConfig() || IsXagmanOwnerServerMatchingActive())
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
                    $"Collection-first start needs known AutoRetainer registration for every selected owner; {unknownCapabilityOwners.Count} owner(s) are unknown.";
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
            plugin.TaskRunner.ClearLog();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        if (!resumeFromStandby && IsXagmanCollectionFirstCollectionPhase())
        {
            plugin.TaskRunner.AddLog(
                $"Xagman: collection-first run {xagmanRunId} planned {xagmanCollectionFirstCollectionPlan.Count} collection candidate(s) and {xagmanCollectionFirstRestockPlan.Count} restock candidate(s) from {xagmanCollectionFirstOwnerFullPlan.Count} selected owner(s).");
        }
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession(resumeFromStandby ? "Franchise Owner standby resume" : "Franchise Owner start"))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = xagmanTradeSafetySessionActive
                    ? "Could not establish safe Dropbox/refusal coordination; Dropbox auto-accept state is unknown and refusal remains suppressed."
                    : "Dropbox auto-accept is confirmed off, but the requested manual/idle refusal state could not be established.";
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
        xagmanStatusText = "Standing by for Tony meetup acknowledgement.";
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
            xagmanStatusText = "No collection candidates on this FO client; waiting for every FO client before restock.";
            plugin.TaskRunner.AddLog($"Xagman: collection-first run {xagmanRunId} has no collection candidates on this client; collection phase acknowledged.");
            PublishXagmanPresence();
            return true;
        }
        if (!resumeFromStandby)
            SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        TryResolveXagmanMeetDestinationForOwner();
        if (ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(xagmanActiveCharacter)
            && !TryRequireXagmanReceiverAutoAccept($"owner {xagmanActiveCharacter} pending Tony supply"))
        {
            return false;
        }
        PublishXagmanPresence();
        var steps = BuildXagmanFranchiseSteps(selected, startIndex);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: OnXagmanFranchiseTaskFinished, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = GetXagmanLocalOwnerCompletedCharacters();
        return true;
    }

    private void SetXagmanRunning(bool value)
    {
        if (!value)
            ClearXagmanExpectedTravelLogoutWindow();

        if (xagmanRunning == value)
        {
            plugin.TargetCommandFix.SetRequiredByXagman(value);
            return;
        }

        xagmanRunning = value;
        InvalidateXagmanTradeCapacityForecast();
        plugin.TargetCommandFix.SetRequiredByXagman(value);
        if (value)
            plugin.TaskRunner.AddLog("Xagman: Fix /target Command is required while Xagman is running.");
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
            && !TryValidateXagmanServerMatchingTonyRoute(
                entry.CharacterNameWorld,
                xagmanSweepRegion,
                xagmanSweepDataCenter,
                out var serverRouteFailure))
        {
            ReportXagmanTravelRouteError(
                $"Server Matching startup rejected {entry.CharacterNameWorld}: {serverRouteFailure}");
            return;
        }
        if (!TryValidateXagmanMeetTravel(entry.CharacterNameWorld, meetWorld, out var routeFailure))
        {
            ReportXagmanTravelRouteError(
                $"Tony startup rejected {entry.CharacterNameWorld} -> {meetWorld}: {routeFailure}");
            return;
        }

        xagmanActiveCharacter = entry.CharacterNameWorld;
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
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true);
        UpdateXagmanTonyTaskRunnerProgress();
    }

    private void ResetXagmanTonyMeetRetryState()
    {
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

        void FailTonyStartup(string message)
        {
            if (startupFailed)
                return;

            startupFailed = true;
            ResetXagmanTonyMeetRetryState();
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = message;
            runner.AddLog(message);
            if (!TryEndXagmanTradeSafetySession("Tony startup failure"))
            {
                runner.AddLog(xagmanTradeSafetySessionActive
                    ? "Xagman: Dropbox auto-accept could not be confirmed off after the startup failure; trade refusal remains suppressed."
                    : "Xagman: Dropbox auto-accept is confirmed off after the startup failure, but the saved manual Refuse Trade Request preference could not be restored.");
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
            Name = $"Xagman Tony Wait For Meetup: {entry.CharacterNameWorld}",
            OnEnter = () =>
            {
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = $"Tony {entry.CharacterNameWorld} is standing by for Franchise Owner meetup relay.";
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} is waiting for Franchise Owner meetup data.");
            },
            IsComplete = HasCompleteXagmanActiveMeetDestination,
            TimeoutSec = 86400f,
        });
        if (includePreflight)
            steps.AddRange(helper.BuildPreFlightOnlySteps(new List<string> { entry.CharacterNameWorld }, runner));
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Begin: {entry.CharacterNameWorld}",
            OnEnter = () =>
            {
                runner.CurrentItemLabel = entry.CharacterNameWorld;
                runner.AddLog($"Xagman: starting Tony {entry.CharacterNameWorld}.");
                xagmanStatus = includePreflight ? XagmanStatus.Preflight : XagmanStatus.Relogging;
                xagmanStatusText = $"Preparing Tony {entry.CharacterNameWorld}.";
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
                xagmanStatusText = $"Relogging to {entry.CharacterNameWorld}.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = $"Traveling {entry.CharacterNameWorld} to {GetXagmanActiveMeetDestinationLabel()}.";
            },
            () =>
            {
                FailTonyStartup($"Xagman: failed to relog Tony {entry.CharacterNameWorld}.");
            });
        AddXagmanTeleportSteps(
            steps,
            "Meet",
            GetXagmanActiveMeetDestinationCommand,
            runner,
            () => IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()),
            false,
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = $"Traveling {entry.CharacterNameWorld} to {GetXagmanActiveMeetDestinationLabel()}.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = $"Tony {entry.CharacterNameWorld} is staged at {GetXagmanActiveMeetDestinationLabel()}.";
            },
            () =>
            {
                FailTonyStartup($"Xagman: failed to travel Tony {entry.CharacterNameWorld} to {GetXagmanActiveMeetDestinationLabel()}.");
            },
            null,
            waitStartTimeoutSec: 600f,
            reissueWhileWaiting: true,
            expectCrossDataCenterLogout: true,
            travelSourceCharacterProvider: () => entry.CharacterNameWorld,
            travelDestinationWorldProvider: GetXagmanActiveMeetWorld);
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Ready: {entry.CharacterNameWorld}",
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = () =>
            {
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} is ready for queue processing.");
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = $"Tony {entry.CharacterNameWorld} ready at the meet spot.";
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
            Name = $"Xagman Tony Prime Dropbox Open: {entry.CharacterNameWorld}",
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = OpenXagmanDropboxWindow,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Prime Dropbox Open Item Tab: {entry.CharacterNameWorld}",
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = OpenXagmanDropboxTradeTab,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Tony Prime Dropbox Open Item Tab Wait: {entry.CharacterNameWorld}", 1.0f, ShouldSkipTonyStartupPostTravel));
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Prime Dropbox Clear Queue: {entry.CharacterNameWorld}",
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = ClearXagmanDropbox,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Publish Ready: {entry.CharacterNameWorld}",
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
        xagmanExpectedLogout = false;
        if (xagmanRunning || xagmanOwnerRunPlan.Count > 0 || xagmanTonyRunPlan.Count > 0)
            CaptureXagmanRunSnapshot();
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();
        ResetXagmanTonyMeetRetryState();
        var tradeSafetyClosed = TryEndXagmanTradeSafetySession("Xagman stop");
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        xagmanActiveRole = plugin.Configuration.XagmanRole;
        xagmanStatus = tradeSafetyClosed ? XagmanStatus.Idle : XagmanStatus.Error;
        xagmanStatusText = tradeSafetyClosed
            ? "Idle"
            : xagmanTradeSafetySessionActive
                ? "Stopped, but Dropbox auto-accept could not be confirmed off; trade refusal remains suppressed."
                : "Stopped after confirming Dropbox auto-accept off, but the saved manual Refuse Trade Request preference could not be restored.";
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
        bool expectLogoutOrExit)
    {
        steps.Add(new TaskStep
        {
            Name = "Xagman Close Trade Safety",
            OnEnter = () =>
            {
                xagmanExpectedLogout = false;
                if (!TryEndXagmanTradeSafetySession(contextLabel))
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = xagmanTradeSafetySessionActive
                        ? "Completion could not confirm Dropbox auto-accept off; trade refusal remains suppressed."
                        : "Completion confirmed Dropbox auto-accept off, but the saved manual Refuse Trade Request preference could not be restored.";
                    runner.AddLog($"Xagman: {xagmanStatusText}");
                    runner.Cancel();
                    return;
                }

                xagmanExpectedLogout = expectLogoutOrExit;
            },
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
                await Plugin.Framework.Run(() => FinalizeXagmanLocalShutdown(reason, disconnectBeforeStop)).ConfigureAwait(false);
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
        if (IsXagmanCollectionFirstCollectionPhase())
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

        var unresolvedOwnerStates = GetXagmanRelevantOwnerPeersForTony(tonyCharacter, true)
            .Select(peer =>
            {
                var name = GetXagmanPeerDisplayName(peer);
                var requestedCount = Math.Max(0, peer.RequestedItems?.Count ?? 0);
                var requestedUnits = peer.RequestedItems == null
                    ? 0
                    : peer.RequestedItems.Sum(request => Math.Max(0, request.Quantity));
                var remainingCharacters = Math.Max(0, peer.TotalCharacters - peer.CompletedCharacters);
                var hasQueuedWork = peer.QueueRequestedAtUtc > DateTime.MinValue;
                var hasActiveStatus = peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading or XagmanStatus.Standby or XagmanStatus.Paused;
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

    private void StartXagmanFailureRecallTask(string reason)
    {
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();

        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        if (runner.IsRunning)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = $"Failure recall could not start while {runner.CurrentTaskName} is running.";
            runner.AddLog($"Xagman: {xagmanStatusText}");
            return;
        }

        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession("failure recall"))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = "Failure recall could not establish safe Dropbox/refusal coordination.";
            }
            runner.AddLog($"Xagman: {xagmanStatusText}");
            return;
        }

        SetXagmanRunning(true);
        xagmanActiveRole = role;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = reason;
        if (role == XagmanRole.Tony)
            MarkXagmanTonyConsumed(localCharacter);

        var steps = new List<TaskStep>();
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            Name = "Xagman Failure Recall Begin",
            OnEnter = () =>
            {
                var stoppedQueue = TryStopXagmanDropboxTradeQueue();
                var sentEscape = TryAbortXagmanTradeWindow();
                ClearXagmanDropbox();
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "failure recall"))
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
        AddXagmanTeleportSteps(
            steps,
            "Failure Recall FC",
            () => "fc",
            runner,
            null,
            true,
            () =>
            {
                xagmanStatus = XagmanStatus.ReturningHome;
                xagmanStatusText = $"Returning {localCharacter} to FC after Xagman failure.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Xagman failure recall return finished for {localCharacter}.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Xagman failure recall return did not complete cleanly for {localCharacter}.";
                runner.AddLog($"Xagman: failure recall could not confirm /li fc return for {localCharacter}.");
            },
            expectCrossDataCenterLogout: true);
        AddXagmanTradeSafetyCompletionStep(
            steps,
            runner,
            "failure recall completion",
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete));
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: () => FinalizeXagmanLocalShutdown("failure recall"), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void StartXagmanFranchiseCompletionTask(string reason)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
            return;
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();

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
        xagmanStatusText = reason;
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            Name = "Xagman Franchise Completion Begin",
            OnEnter = () =>
            {
                var stoppedQueue = TryStopXagmanDropboxTradeQueue();
                var sentEscape = TryAbortXagmanTradeWindow();
                ClearXagmanDropbox();
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "Franchise Owner peer completion"))
                    return;
                SetXagmanRunning(false);
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = reason;
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
                runner.CompletedItems = runner.TotalItems;
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
        if (IsXagmanCollectionFirstRestockPhase() && xagmanCompletionDirectiveAcknowledged)
        {
            // Keep the peer connected long enough for the hub and Tony to observe the scoped
            // acknowledgement before optional FC return or local peer shutdown begins.
            steps.Add(MonthlyReloggerTask.MakeDelay(
                "Xagman Franchise Completion Ack Relay",
                2.5f));
        }
        if (cfg.XagmanAutoReturnToFc)
        {
            AddXagmanTeleportSteps(
                steps,
                "Franchise Completion FC",
                () => "fc",
                runner,
                null,
                true,
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Returning owner {localCharacter} to FC.";
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Owner {localCharacter} return-to-FC attempt finished.";
                    RememberXagmanRecentFcReturn(localCharacter);
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Owner {localCharacter} return-to-FC attempt failed to start cleanly.";
                },
                expectCrossDataCenterLogout: true);
        }
        steps.Add(new TaskStep
        {
            Name = "Xagman Franchise Disconnect Peer Service",
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
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete));
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: () => FinalizeXagmanLocalShutdown("Franchise Owner peer completion"), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private List<TaskStep> BuildXagmanFranchiseSteps(List<string> characters, int startIndex)
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
        var collectionOnly = IsXagmanCollectionFirstCollectionPhase();
        var restockOnly = IsXagmanCollectionFirstRestockPhase();
        runner.TotalItems = characters.Count;
        runner.CompletedItems = Math.Max(0, Math.Min(startIndex, characters.Count));
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            Name = "Xagman Owner Wait For Tony Meetup Ack",
            OnEnter = () =>
            {
                var partnerName = GetCharacterNameFromKey(xagmanPreferredTonyCharacter);
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = string.IsNullOrWhiteSpace(partnerName)
                    ? "Franchise Owner is standing by for Tony meetup acknowledgement."
                    : $"Franchise Owner is standing by for Tony {partnerName} meetup acknowledgement.";
                runner.AddLog(string.IsNullOrWhiteSpace(partnerName)
                    ? "Xagman: Franchise Owner is waiting for Tony to start and relay meetup data."
                    : $"Xagman: Franchise Owner is waiting for Tony {partnerName} to start and relay meetup data.");
            },
            IsComplete = () => xagmanOwnerStartRequested && IsXagmanFranchiseStartupReady(),
            TimeoutSec = 86400f,
        });
        steps.AddRange(helper.BuildPreFlightOnlySteps(characters.Skip(startIndex).ToList(), runner));
        for (var i = startIndex; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;
            var relogFailed = false;
            var standbyRequested = false;
            var charSkipped = false;
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
                var detail = !invalid.GreenScanComplete
                    ? invalid.GreenScanError
                    : $"unsupported protocol {invalid.GreenValueProtocolRevision}";
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Owner {charName} green-value scan failed closed: {detail}";
                if (!runner.FailedCharacters.Contains(charName))
                    runner.FailedCharacters.Add(charName);
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
                        xagmanStatusText = $"Owner {charName} is moving back into trade range with Tony {partnerName}.";

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
                        xagmanStatusText = $"Owner {charName} is moving back into trade range with Tony {partnerName} ({tooFarAwayRetryCount}/{maxOwnerTradeRangeRetries}).";
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
                if (standbyRequested || relogFailed || !ShouldXagmanOwnerStandbyForTonyRotation(charName))
                    return standbyRequested;
                return BeginStandbyForTonyRotation(string.Empty);
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
                if (!xagmanRunning || relogFailed || standbyRequested || charSkipped || xagmanOwnerPauseForTonyRotationRequested)
                    return true;
                if (YieldOwnerTradeLockIfNeeded())
                    return true;
                return TryEnterStandby();
            }

            bool ShouldSkipOwnerCollectionTradeExecution()
            {
                return restockOnly || ShouldSkipTradeFlow() || ownerCollectionQueuedEntries <= 0;
            }

            bool ShouldSkipOwnerCollectionSetup()
            {
                return restockOnly || ShouldSkipTradeFlow();
            }

            bool ShouldSkipRequestedTradeFlow()
            {
                return collectionOnly || ShouldSkipTradeFlow() || xagmanOwnerRequestedItems.Count == 0;
            }

            bool ShouldArmOwnerAutoAcceptForPendingTonySupply()
                => !collectionOnly && ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(charName);

            void AdvertiseOwnerTradeLockWait(string phaseLabel)
            {
                var waitingText = $"Owner {charName} is waiting for Tony to confirm {phaseLabel}.";
                if (xagmanStatus == XagmanStatus.Called
                    && xagmanStatusText.Equals(waitingText, StringComparison.Ordinal))
                {
                    return;
                }

                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = waitingText;
                runner.AddLog($"Xagman: owner {charName} is waiting for Tony to reacquire the explicit trade lock for {phaseLabel}.");
                PublishXagmanPresence();
            }

            void AddOwnerTradeLockWaitStep(string phaseLabel, Func<bool> shouldSkip)
            {
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Wait For Tony Trade Lock ({phaseLabel}): {charName}",
                    ShouldSkip = shouldSkip,
                    OnEnter = () =>
                    {
                        if (!shouldSkip() && !HasXagmanActiveTonyTradeLock(charName))
                            AdvertiseOwnerTradeLockWait(phaseLabel);
                    },
                    IsComplete = () => shouldSkip()
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
                xagmanOwnerCurrentCharacterIndex = i;
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

            void EnterOwnerTonyQueue(bool logQueueEntry, string? statusTextOverride = null)
            {
                if (xagmanQueueRequestedAtUtc == DateTime.MinValue)
                    xagmanQueueRequestedAtUtc = DateTime.UtcNow;
                xagmanPreferredTonyCharacter = string.Empty;
                if (xagmanStatus is not (XagmanStatus.Called or XagmanStatus.Trading))
                    xagmanStatus = resumingStandbyOwner ? XagmanStatus.Standby : XagmanStatus.WaitingRoom;
                xagmanStatusText = !string.IsNullOrWhiteSpace(statusTextOverride)
                    ? statusTextOverride
                    : resumingStandbyOwner
                        ? $"Owner {charName} is on standby for the next Tony."
                        : $"Owner {charName} is in Tony's waiting room.";
                if (logQueueEntry)
                {
                    runner.AddLog(resumingStandbyOwner
                        ? $"Xagman: standby owner {charName} is waiting for the next Tony to resume them."
                        : $"Xagman: waiting for the next Tony to call {charName}.");
                }
                PublishXagmanPresence();
            }

            bool PollOwnerTradeWait()
            {
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
                    xagmanStatusText = adoptedReplacementTony
                        ? $"Owner {charName} is reapproaching replacement Tony {partnerName} for requested supply."
                        : $"Owner {charName} is holding position with Tony {partnerName} for requested supply.";
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
                if (restockOnly)
                {
                    ownerCollectionRetryRequested = false;
                    return;
                }
                ownerCollectionRetryRequested = HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName);
                if (!ownerCollectionRetryRequested)
                    return;

                SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                if (collectionPassNumber >= maxOwnerCollectionTradePasses)
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Owner {charName} still has give items remaining after {collectionPassNumber} trade passes.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                    runner.AddLog($"Xagman: owner {charName} still has give items remaining after collection pass {collectionPassNumber}; stopping before Tony supply requests.");
                    return;
                }

                runner.AddLog($"Xagman: owner {charName} still has give items remaining after collection pass {collectionPassNumber}; retrying owner give trade before any Tony supply requests.");
            }

            bool EvaluateOwnerSendoffReconciliation(int verificationPassNumber, bool yieldOnFailure)
            {
                ownerSendoffVerified = false;
                ownerCollectionRetryRequested = !restockOnly
                    && HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName);
                var remainingRequestedItems = collectionOnly
                    ? new List<XagmanTradeRequestEntry>()
                    : BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
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
                var stepSuffix = $" [pass {collectionPassNumber}]";
                var repeatedCollectionQueuedEntries = 0;

                bool ShouldSkipRepeatedTradeFlow()
                {
                    if (!ownerCollectionRetryRequested)
                        return true;
                    return ShouldSkipTradeFlow();
                }

                bool ShouldSkipRepeatedTradeExecution()
                {
                    return ShouldSkipRepeatedTradeFlow() || repeatedCollectionQueuedEntries <= 0;
                }

                steps.Add(new TaskStep
                {
                    Name = $"Xagman Approach Tony: {charName}{stepSuffix}",
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        xagmanStatus = XagmanStatus.Called;
                        xagmanStatusText = $"Approaching Tony for {charName}.";
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
                        xagmanStatusText = $"Failed to reach Tony for {charName}.";
                        if (!runner.FailedCharacters.Contains(charName))
                            runner.FailedCharacters.Add(charName);
                    },
                });
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Tony Arrival Settle: {charName}{stepSuffix}", 0.5f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Open Dropbox: {charName}{stepSuffix}",
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                        xagmanStatus = XagmanStatus.Trading;
                        xagmanStatusText = $"Owner {charName} is trading with Tony {partnerName}.";
                        OpenXagmanDropboxWindow();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Open Item Tab: {charName}{stepSuffix}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Open Item Tab Wait: {charName}{stepSuffix}", 1.0f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Clear Queue: {charName}{stepSuffix}",
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
                    Name = $"Xagman Trade Queue Items: {charName}{stepSuffix}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Queue Wait: {charName}{stepSuffix}", 0.5f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Retarget: {charName}{stepSuffix}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Retarget Wait: {charName}{stepSuffix}", 0.1f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Focus Target: {charName}{stepSuffix}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Focus Wait: {charName}{stepSuffix}", 0.15f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Confirm Arrival: {charName}{stepSuffix}",
                    ShouldSkip = ShouldSkipRepeatedTradeExecution,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeExecution())
                            return;
                        xagmanStatus = XagmanStatus.Called;
                        xagmanStatusText = $"Confirming Tony arrival for {charName}.";
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
                        xagmanStatusText = $"Failed to settle next to Tony for {charName}.";
                        if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                    },
                });
                steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Confirm Arrival Wait: {charName}{stepSuffix}", 0.5f, ShouldSkipRepeatedTradeExecution));
                AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Trade {charName}{stepSuffix}", false, ShouldSkipRepeatedTradeExecution);
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Start: {charName}{stepSuffix}",
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
                            xagmanStatusText = $"Failed to start Dropbox trading queue for owner {charName}.";
                            if (!runner.FailedCharacters.Contains(charName))
                                runner.FailedCharacters.Add(charName);
                            return;
                        }
                        xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Wait: {charName}{stepSuffix}",
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
                        xagmanStatusText = $"Trade timed out for owner {charName}.";
                        if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                        CleanupXagmanDropboxTradeAttempt($"owner {charName} repeated give trade timeout");
                    },
                });
                AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Trade {charName}{stepSuffix}", false, () => standbyRequested || !ownerCollectionRetryRequested || repeatedCollectionQueuedEntries <= 0);
                steps.Add(new TaskStep
                {
                    Name = $"Xagman Trade Verify Remaining Give Items: {charName}{stepSuffix}",
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
                Name = $"Xagman Owner Server Gate: {charName}",
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

                        if (TryGetXagmanFixedMeetRouteFailureForOwner(charName, out var routeFailure))
                        {
                            relogFailed = true;
                            xagmanStatus = XagmanStatus.Error;
                            xagmanStatusText = $"Owner {charName} cannot reach the advertised Tony meetup: {routeFailure}";
                            if (!runner.FailedCharacters.Contains(charName))
                                runner.FailedCharacters.Add(charName);
                            runner.AddLog($"Xagman: {xagmanStatusText}");
                            if (IsXagmanCollectionFirstRunActive())
                            {
                                ReportXagmanTravelRouteError(
                                    $"coordinated owner {charName} cannot reach the advertised fixed-world meetup: {routeFailure}");
                            }
                            return true;
                        }

                        if (xagmanStatus != XagmanStatus.Paused)
                            PublishXagmanPresence();
                        xagmanStatus = XagmanStatus.Paused;
                        xagmanStatusText = $"Owner {charName}: waiting for a fresh complete Tony meet destination.";
                        return false;
                    }

                    xagmanOwnerSweepPendingDataCenter = GetXagmanDataCenterOfChar(charName);
                    var decision = EvaluateXagmanOwnerServerGate(charName, out var gateMeetWorld, out var gateMeetAetheryte, out var gateReason);
                    switch (decision)
                    {
                        case XagmanOwnerServerGate.Proceed:
                            if (!HasCompleteXagmanMeetDestination(gateMeetWorld, gateMeetAetheryte))
                            {
                                xagmanStatus = XagmanStatus.Paused;
                                xagmanStatusText = $"Owner {charName}: waiting for Tony to publish a complete meet world and aetheryte.";
                                return false;
                            }
                            SetXagmanActiveMeetDestination(gateMeetWorld, gateMeetAetheryte);
                            return true;
                        case XagmanOwnerServerGate.RouteError:
                            charSkipped = true;
                            ReportXagmanTravelRouteError(
                                $"coordinated owner {charName} cannot enter Server Matching: {gateReason}");
                            return true;
                        case XagmanOwnerServerGate.Skip:
                            charSkipped = true;
                            MarkXagmanOwnerSkipped(charName, gateReason);
                            return true;
                        default:
                            if (xagmanStatus != XagmanStatus.Paused)
                                PublishXagmanPresence();
                            xagmanStatus = XagmanStatus.Paused;
                            xagmanStatusText = $"Owner {charName}: {gateReason}.";
                            return false;
                    }
                },
                TimeoutSec = 86400f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Begin: {charName}",
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"Xagman: processing owner {charName} ({charIndex}/{charTotal}).");
                    xagmanActiveCharacter = charName;
                    xagmanOwnerCurrentCharacterIndex = i;
                    xagmanStatus = XagmanStatus.Relogging;
                    xagmanStatusText = $"Relogging owner {charName}.";
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
                    // Active-processing timer starts at the /ays relog attempt (after the wait-for-server
                    // gate), so idle time waiting for the Tony to reach this server is not counted.
                    RecordXagmanOwnerProcessingStart(charName);
                    xagmanStatus = XagmanStatus.Relogging;
                    xagmanStatusText = $"Relogging owner {charName}.";
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = $"Traveling owner {charName} to {GetXagmanActiveMeetDestinationLabel()}.";
                },
                () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to relog owner {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
                () => relogFailed || charSkipped);
            AddXagmanOwnerMeetTravelSteps(
                steps,
                charName,
                runner,
                () =>
                {
                    if (relogFailed)
                        return;
                    xagmanStatus = XagmanStatus.ReadyForQueue;
                    xagmanStatusText = $"Owner {charName} reached the meet spot.";
                },
                () =>
                {
                    if (relogFailed)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Owner {charName} failed to reach the meet spot.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
                reason =>
                {
                    if (relogFailed || charSkipped)
                        return;
                    charSkipped = true;
                    MarkXagmanOwnerSkipped(charName, reason);
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = $"Owner {charName} skipped: {reason}.";
                },
                () => relogFailed || charSkipped);
            steps.Add(new TaskStep
            {
                Name = $"Xagman Wait For Tony Available: {charName}",
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(GetXagmanOwnerQueueTonyCharacter());
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = string.IsNullOrWhiteSpace(partnerName)
                        ? $"Owner {charName} is waiting for Tony to become available at the meet spot."
                        : $"Owner {charName} is waiting for Tony {partnerName} to become available.";
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
                    xagmanStatusText = $"Timed out waiting for Tony availability for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Approach Tony Wait Spot: {charName}",
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(GetXagmanOwnerQueueTonyCharacter());
                    EnterOwnerTonyQueue(
                        false,
                        string.IsNullOrWhiteSpace(partnerName)
                            ? $"Owner {charName} is waiting for Tony to become available at the meet spot."
                            : $"Owner {charName} is queued and moving into position near Tony {partnerName}.");
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
                        EnterOwnerTonyQueue(false, $"Owner {charName} is waiting for Tony to become available at the meet spot.");
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
                    xagmanStatusText = string.IsNullOrWhiteSpace(partnerName)
                        ? $"Failed to reach Tony for {charName}."
                        : $"Failed to reach Tony {partnerName} for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Queue Wait: {charName}",
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
                    xagmanStatusText = $"Queue wait timed out for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            AppendXagmanDropboxAutoAcceptStep(
                steps,
                $"Xagman Tony Supply Handoff {charName}",
                true,
                () => relogFailed || !ShouldArmOwnerAutoAcceptForPendingTonySupply(),
                requireSuccess: true,
                onFailure: () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to enable Dropbox auto-accept for Tony supply handoff {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Approach Tony: {charName}",
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Approaching Tony for {charName}.";
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
                    xagmanStatusText = $"Failed to reach Tony for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Tony Arrival Settle: {charName}", 0.5f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Open Dropbox: {charName}",
                ShouldSkip = ShouldSkipOwnerCollectionSetup,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionSetup())
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = $"Owner {charName} is trading with Tony {partnerName}.";
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Open Item Tab: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Open Item Tab Wait: {charName}", 1.0f, ShouldSkipOwnerCollectionSetup));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Clear Queue: {charName}",
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
                Name = $"Xagman Trade Queue Items: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Queue Wait: {charName}", 0.5f, ShouldSkipOwnerCollectionSetup));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Retarget: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Retarget Wait: {charName}", 0.1f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Focus Target: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Focus Wait: {charName}", 0.15f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Confirm Arrival: {charName}",
                ShouldSkip = ShouldSkipOwnerCollectionTradeExecution,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionTradeExecution())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Confirming Tony arrival for {charName}.";
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
                    xagmanStatusText = $"Failed to settle next to Tony for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                    runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Confirm Arrival Wait: {charName}", 0.5f, ShouldSkipOwnerCollectionTradeExecution));
            AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Trade {charName}", false, ShouldSkipOwnerCollectionTradeExecution);
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Start: {charName}",
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
                        xagmanStatusText = $"Failed to start Dropbox trading queue for owner {charName}.";
                        if (!runner.FailedCharacters.Contains(charName))
                            runner.FailedCharacters.Add(charName);
                        return;
                    }
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Wait: {charName}",
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
                    xagmanStatusText = $"Trade timed out for owner {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                    runner.FailedCharacters.Add(charName);
                    CleanupXagmanDropboxTradeAttempt($"owner {charName} give trade timeout");
                },
            });
            AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Trade {charName}", false, () => standbyRequested || ownerCollectionQueuedEntries <= 0 || ShouldArmOwnerAutoAcceptForPendingTonySupply());
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Verify Remaining Give Items: {charName}",
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
                Name = $"Xagman Trade Build Requests: {charName}",
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
                Name = $"Xagman Requested Trade Open Dropbox: {charName}",
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = string.IsNullOrWhiteSpace(partnerName)
                        ? $"Owner {charName} is waiting for Tony to supply requested items."
                        : $"Owner {charName} is waiting for Tony {partnerName} to supply requested items.";
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Requested Trade Open Item Tab: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Requested Trade Open Item Tab Wait: {charName}", 1.0f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Requested Trade Clear Queue: {charName}",
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
                Name = $"Xagman Requested Trade Retarget: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Requested Trade Retarget Wait: {charName}", 0.1f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Requested Trade Focus Target: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Requested Trade Focus Wait: {charName}", 0.15f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Requested Trade Confirm Arrival: {charName}",
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Confirming Tony arrival for {charName} requested trade.";
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
                    xagmanStatusText = $"Failed to settle next to Tony for requested trade {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Requested Trade Confirm Arrival Wait: {charName}", 0.5f, ShouldSkipRequestedTradeFlow));
            AppendXagmanDropboxAutoAcceptStep(
                steps,
                $"Xagman Requested Trade {charName}",
                true,
                ShouldSkipRequestedTradeFlow,
                requireSuccess: true,
                onFailure: () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to enable Dropbox auto-accept for requested trade {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Requested Trade Receiver Ready: {charName}",
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
                Name = $"Xagman Requested Trade Wait: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested || xagmanOwnerRequestedItems.Count == 0,
                OnEnter = () => xagmanObservedDropboxBusy = false,
                IsComplete = () => relogFailed || standbyRequested || xagmanOwnerRequestedItems.Count == 0 || PollOwnerRequestedTradeWait() || TryEnterStandby(),
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Requested trade timed out for owner {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                    CleanupXagmanDropboxTradeAttempt($"owner {charName} requested trade timeout");
                },
            });
            AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Requested Trade {charName}", false, () => standbyRequested || charSkipped);
            steps.Add(new TaskStep
            {
                Name = $"Xagman Completion Verify 1: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested,
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    EvaluateOwnerSendoffReconciliation(1, false);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Completion Verify Wait: {charName}", 0.75f, () => relogFailed || standbyRequested));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Completion Verify 2: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested,
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
                Name = $"Xagman Trade Release Queue Slot: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested || !ownerSendoffVerified,
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
                    "FC",
                    () => "fc",
                    runner,
                    null,
                    true,
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Returning owner {charName} to FC.";
                    },
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Owner {charName} return-to-FC attempt finished.";
                        RememberXagmanRecentFcReturn(charName);
                    },
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Owner {charName} return-to-FC attempt failed to start cleanly.";
                    },
                    () => relogFailed || standbyRequested || charSkipped,
                    expectCrossDataCenterLogout: true);
            }
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Complete: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested || charSkipped,
                OnEnter = () =>
                {
                    if (!collectionOnly && xagmanOwnerCompletedKeys.Add(charName))
                        InvalidateXagmanTradeCapacityForecast();
                    xagmanOwnerCompletedCharacters = charIndex;
                    if (IsXagmanCollectionFirstRunActive())
                        xagmanPhaseResolvedCharacters = Math.Max(xagmanPhaseResolvedCharacters, charIndex);
                    runner.CompletedItems = charIndex;
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
                        xagmanStatus = charIndex >= charTotal ? XagmanStatus.Completed : XagmanStatus.Idle;
                        xagmanStatusText = $"Owner {charName} completed successfully.";
                    }
                    if (!collectionOnly)
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
                Name = $"Xagman Owner Timing: {charName}",
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () => RecordXagmanOwnerProcessingEnd(charName),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }
        steps.Add(new TaskStep
        {
            Name = "Xagman Franchise Summary",
            ShouldSkip = () => xagmanOwnerStandbyPending,
            OnEnter = () =>
            {
                if ((collectionOnly || restockOnly) && xagmanTravelRouteFatalError)
                {
                    xagmanPhaseComplete = false;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText =
                        $"{xagmanRunPhase} pass stopped on an invalid or unreachable meetup route; the coordinated phase was not acknowledged.";
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
                    xagmanStatusText = runner.FailedCharacters.Count == 0
                        ? "Collection pass complete; waiting for every FO client before restock."
                        : $"Collection pass resolved with {runner.FailedCharacters.Count} failure(s); waiting at the global restock barrier.";
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
                    xagmanStatusText = runner.FailedCharacters.Count == 0
                        ? "Restock pass complete; waiting for every FO client before final cleanup."
                        : $"Restock pass resolved with {runner.FailedCharacters.Count} failure(s); waiting at the global completion barrier.";
                    runner.SuppressLogoutCancel = true;
                    runner.AddLog(
                        $"Xagman: restock phase complete for run {xagmanRunId}; this FO client is holding its connection until Tony completes the global barrier.");
                    PublishXagmanPresence();
                    return;
                }
                SetXagmanRunning(false);
                xagmanOwnerCurrentCharacterIndex = characters.Count;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = runner.FailedCharacters.Count == 0
                    ? "Franchise Owner run completed."
                    : $"Franchise Owner run finished with {runner.FailedCharacters.Count} failures.";
                runner.SuppressLogoutCancel = MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete);
                PublishXagmanPresence();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (collectionOnly || restockOnly)
        {
            // Coordinated phases must remain connected with trade-safety state intact while Tony
            // observes the phase acknowledgement. Tony sends the scoped completion directive
            // after the global restock barrier, which owns the one final cleanup sequence.
            return steps;
        }
        steps.Add(new TaskStep
        {
            Name = "Xagman Franchise Signal Tony Completion",
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
            Name = "Xagman Franchise Disconnect Peer Service",
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

    private void AddXagmanRelogSteps(List<TaskStep> steps, string charName, TaskRunner runner, SysAction onEnter, SysAction onReady, SysAction onTimeout, Func<bool>? externalSkip = null)
    {
        var relogFailed = false;
        var skipRelog = false;
        var recentlyReturnedToFc = false;
        var returnToFcBeforeRelog = false;
        var loggedInCharacter = string.Empty;
        bool ShouldExternalSkip() => externalSkip?.Invoke() ?? false;
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog Setup: {charName}",
            ShouldSkip = ShouldExternalSkip,
            OnEnter = () =>
            {
                xagmanExpectedLogout = false;
                onEnter();
                skipRelog = false;
                returnToFcBeforeRelog = false;
                loggedInCharacter = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                if (loggedInCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase))
                {
                    skipRelog = true;
                    runner.AddLog($"Xagman: already on {charName}.");
                    return;
                }

                recentlyReturnedToFc = plugin.Configuration.XagmanAutoReturnToFc
                    && ConsumeXagmanRecentFcReturn(loggedInCharacter);
                returnToFcBeforeRelog = plugin.Configuration.XagmanAutoReturnToFc
                    && !recentlyReturnedToFc
                    && !string.IsNullOrWhiteSpace(loggedInCharacter);
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
            $"Return FC Before Relog: {charName}",
            () => "fc",
            runner,
            null,
            false,
            () =>
            {
                if (returnToFcBeforeRelog)
                    runner.AddLog($"Xagman: sending /li fc for {loggedInCharacter} before /ays relog {charName}.");
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
                relogFailed = true;
                runner.AddLog($"Xagman: /li fc did not complete before relogging to {charName}; aborting relog.");
                onTimeout();
            },
            () => relogFailed || skipRelog || !returnToFcBeforeRelog || ShouldExternalSkip(),
            expectCrossDataCenterLogout: true);
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog: {charName}",
            ShouldSkip = () => relogFailed || skipRelog || ShouldExternalSkip(),
            OnEnter = () =>
            {
                runner.AddLog(returnToFcBeforeRelog
                    ? $"Xagman: relogging to {charName} after returning {loggedInCharacter} to FC."
                    : $"Xagman: relogging to {charName}.");
                xagmanExpectedLogout = true;
                ChatHelper.SendMessage($"/ays relog {charName}");
            },
            IsComplete = () => MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                && CharacterSafetyHelper.IsCharacterSafeWaitReady(),
            TimeoutSec = 600f,
            MaxRetries = 2,
            OnTimeout = () =>
            {
                xagmanExpectedLogout = false;
                relogFailed = true;
                onTimeout();
            },
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog Logout Window Close: {charName}",
            OnEnter = () => xagmanExpectedLogout = false,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog Ready: {charName}",
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
        ownerShouldSkip = false;
        var serverMatchingTony = GetXagmanServerMatchingTonyPeer();
        if (serverMatchingTony != null)
            xagmanServerMatchingActive = true;

        if (xagmanServerMatchingActive)
        {
            var decision = EvaluateXagmanOwnerServerGate(charName, out meetWorld, out meetAetheryte, out reason);
            if (decision == XagmanOwnerServerGate.Proceed
                && HasCompleteXagmanMeetDestination(meetWorld, meetAetheryte))
            {
                return true;
            }

            ownerShouldSkip = decision == XagmanOwnerServerGate.Skip;
            meetWorld = string.Empty;
            meetAetheryte = string.Empty;
            if (string.IsNullOrWhiteSpace(reason))
                reason = "waiting for a fresh complete Server Matching destination";
            return false;
        }

        if (TryGetXagmanMeetDestinationForOwner(out meetWorld, out meetAetheryte, charName)
            && HasCompleteXagmanMeetDestination(meetWorld, meetAetheryte))
        {
            reason = string.Empty;
            return true;
        }

        meetWorld = string.Empty;
        meetAetheryte = string.Empty;
        reason = "waiting for a fresh complete Tony meet destination";
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

        bool ShouldExternalSkip()
        {
            var shouldSkip = externalSkip?.Invoke() ?? false;
            if (shouldSkip)
                ClearXagmanExpectedTravelLogoutWindow();
            return shouldSkip;
        }
        bool ShouldStopTravel() => travelComplete || travelFailed || ShouldExternalSkip();
        void FailTravelBeforeCommand(string reason)
        {
            if (ShouldStopTravel())
                return;

            ClearXagmanExpectedTravelLogoutWindow();
            travelFailed = true;
            runner.AddLog($"Xagman: owner {charName} meetup travel stopped before a command could be sent: {reason}");
            onFailure();
            xagmanStatusText = $"Owner {charName} meetup travel could not start safely; moving to the next owner.";
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
                    xagmanStatusText = $"Retrying owner {charName} travel to {attemptDestinationLabel} ({capturedAttempt + 1}/{maxAttempts}).";
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
                xagmanStatusText = stillOnExpectedCharacter || retryThroughPreCommandGate
                    ? $"Owner {charName} failed meetup travel after {capturedAttempt}/{maxAttempts} attempts; moving to the next owner."
                    : $"Owner {charName} is no longer logged in; meetup travel stopped and the next owner will run.";
            }

            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Meet Travel Command: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
                ShouldSkip = ShouldStopTravel,
                OnEnter = () =>
                {
                    if (ShouldStopTravel())
                        return;

                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = $"Owner {charName} is waiting for a fresh complete meetup destination.";
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
                        xagmanStatusText = $"Owner {charName} is still loading or traveling ({transitionReason}); waiting before meetup dispatch.";
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
                        xagmanStatusText = $"Owner {charName} identity is temporarily unavailable; waiting before meetup dispatch.";
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
                        xagmanStatusText = $"Confirming owner identity before meetup dispatch ({preCommandMismatchConfirmCount}/{stableObservationPasses}).";
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
                        if (!TryCaptureFreshXagmanOwnerMeetDestination(
                                charName,
                                out attemptWorld,
                                out attemptAetheryte,
                                out preCommandWaitReason,
                                out var ownerShouldSkip))
                        {
                            if (ownerShouldSkip)
                            {
                                ClearXagmanExpectedTravelLogoutWindow();
                                travelFailed = true;
                                runner.AddLog($"Xagman: owner {charName} meetup dispatch skipped without sending a travel command: {preCommandWaitReason}.");
                                onSkip(preCommandWaitReason);
                                return true;
                            }

                            xagmanStatus = XagmanStatus.Paused;
                            xagmanStatusText = $"Owner {charName}: {preCommandWaitReason}.";
                            if (!destinationWaitLogged)
                            {
                                destinationWaitLogged = true;
                                runner.AddLog($"Xagman: owner {charName} meetup dispatch is waiting without consuming a travel attempt: {preCommandWaitReason}.");
                            }
                            return false;
                        }
                    }

                    attemptDestinationLabel = GetPrepLogisticsDestinationLabel(attemptWorld, attemptAetheryte);
                    if (!TryValidateXagmanMeetTravel(charName, attemptWorld, out var routeFailure))
                    {
                        if (IsXagmanCollectionFirstRunActive())
                        {
                            ReportXagmanTravelRouteError(
                                $"coordinated owner {charName} cannot reach pinned destination {attemptDestinationLabel}: {routeFailure}");
                        }
                        FailTravelBeforeCommand(
                            $"the pinned destination {attemptDestinationLabel} is not reachable: {routeFailure}");
                        return true;
                    }

                    SetXagmanActiveMeetDestination(attemptWorld, attemptAetheryte);
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = $"Traveling owner {charName} to {attemptDestinationLabel} ({capturedAttempt}/{maxAttempts}).";
                    if (destinationWaitLogged)
                        runner.AddLog($"Xagman: owner {charName} acquired a fresh meetup destination at {attemptDestinationLabel}; beginning travel attempt {capturedAttempt}/{maxAttempts}.");

                    alreadyAtDestination = IsXagmanAtMeetDestination(attemptWorld, attemptAetheryte);
                    if (alreadyAtDestination)
                    {
                        ClearXagmanExpectedTravelLogoutWindow();
                        runner.AddLog($"Xagman: owner {charName} is already at {attemptDestinationLabel}; running CharacterSafeWait and pinned destination verification.");
                        return true;
                    }

                    var command = $"{attemptWorld}, {attemptAetheryte}";
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
                $"Xagman Owner Meet Travel Init: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
                1f,
                ShouldSkipAttempt));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Meet Travel Wait Start: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
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
                    ClearXagmanExpectedTravelLogoutWindow();
                    startObservationFailure = commandAccepted
                        ? $"Lifestream never reported busy within {waitStartTimeoutSec:0} seconds."
                        : $"{startObservationFailure} No busy state appeared within {waitStartTimeoutSec:0} seconds.".Trim();
                    runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}: {startObservationFailure} Continuing through CharacterSafeWait and destination verification.");
                },
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Meet Travel Wait Complete: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
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
                Name = $"Xagman Owner Meet Travel Confirm Idle: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
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
                         $"Xagman Owner Meet Travel SafeWait: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
                         safeWaitTimeoutSec))
            {
                var originalComplete = safeWait.IsComplete;
                steps.Add(new TaskStep
                {
                    Name = safeWait.Name,
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
                Name = $"Xagman Owner Meet Travel Verify: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
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
                        xagmanStatusText = $"Owner {charName} is still casting, zoning, or loading ({transitionReason}); destination verification is waiting.";
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
                        xagmanStatusText = $"Owner {charName} identity is temporarily unavailable; destination verification is waiting.";
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
                        xagmanStatusText = $"Confirming owner identity after travel ({finalIdentityMismatchConfirmCount}/{stableObservationPasses}).";
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
                        xagmanStatusText = $"Confirming owner {charName} is not at {attemptDestinationLabel} ({finalDestinationMismatchConfirmCount}/{stableObservationPasses}).";
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
                $"Xagman Owner Meet Travel Retry Delay: {charName} [attempt {capturedAttempt}/{maxAttempts}]",
                2f,
                () => ShouldStopTravel() || !retryPending));
        }
    }

    private void AddXagmanTeleportSteps(List<TaskStep> steps, string label, Func<string> commandProvider, TaskRunner runner, Func<bool>? alreadyThere, bool allowNoBusy, SysAction onEnter, SysAction onReady, SysAction onTimeout, Func<bool>? externalSkip = null, float waitStartTimeoutSec = 0f, bool reissueWhileWaiting = false, bool expectCrossDataCenterLogout = false, Func<string>? travelSourceCharacterProvider = null, Func<string>? travelDestinationWorldProvider = null)
    {
        var skipTeleport = false;
        var sawBusy = false;
        var teleportFailed = false;
        var lastReissueUtc = DateTime.MinValue;

        void ClearExpectedTravelLogout()
        {
            if (expectCrossDataCenterLogout)
                ClearXagmanExpectedTravelLogoutWindow();
        }

        bool ShouldExternalSkip()
        {
            var shouldSkip = externalSkip?.Invoke() ?? false;
            if (shouldSkip)
                ClearExpectedTravelLogout();
            return shouldSkip;
        }

        void ExecuteTeleportCommand()
        {
            if (travelSourceCharacterProvider != null && travelDestinationWorldProvider != null)
            {
                var sourceCharacter = travelSourceCharacterProvider();
                var destinationWorld = travelDestinationWorldProvider();
                if (!TryValidateXagmanMeetTravel(sourceCharacter, destinationWorld, out var routeFailure))
                {
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
                ClearExpectedTravelLogout();
                return;
            }

            if (expectCrossDataCenterLogout)
                ExecuteXagmanTravelCommandWithExpectedLogout(command, $"Xagman Lifestream operation ({label})");
            else
                plugin.IpcClient.LifestreamExecuteCommand(command);
        }

        steps.Add(new TaskStep
        {
            Name = $"Xagman Teleport {label}: Command",
            OnEnter = () =>
            {
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
                    ClearExpectedTravelLogout();
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Teleport {label}: Wait Start",
            ShouldSkip = () => skipTeleport || teleportFailed || ShouldExternalSkip(),
            IsComplete = () =>
            {
                if (ShouldExternalSkip())
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
                ClearExpectedTravelLogout();
                if (ShouldExternalSkip())
                {
                    skipTeleport = true;
                    return;
                }
                if (allowNoBusy || (alreadyThere?.Invoke() ?? false))
                {
                    skipTeleport = true;
                    return;
                }
                teleportFailed = true;
                onTimeout();
            },
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Teleport {label}: Wait Complete",
            ShouldSkip = () => teleportFailed || (skipTeleport && !sawBusy) || ShouldExternalSkip(),
            IsComplete = () =>
            {
                if (teleportFailed || ShouldExternalSkip())
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
                ClearExpectedTravelLogout();
                if (ShouldExternalSkip())
                    return;
                if (allowNoBusy || (alreadyThere?.Invoke() ?? false))
                {
                    skipTeleport = true;
                    return;
                }
                teleportFailed = true;
                onTimeout();
            },
        });
        foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Xagman Teleport {label} SafeWait", 30f))
        {
            var originalComplete = safeWait.IsComplete;
            steps.Add(new TaskStep
            {
                Name = safeWait.Name,
                ShouldSkip = () => teleportFailed || ShouldExternalSkip(),
                OnEnter = safeWait.OnEnter,
                IsComplete = () => teleportFailed || ShouldExternalSkip() || originalComplete(),
                TimeoutSec = safeWait.TimeoutSec,
                MaxRetries = safeWait.MaxRetries,
                OnTimeout = () =>
                {
                    ClearExpectedTravelLogout();
                    if (ShouldExternalSkip())
                        return;
                    teleportFailed = true;
                    onTimeout();
                },
            });
        }
        steps.Add(new TaskStep
        {
            Name = $"Xagman Teleport {label}: Ready",
            ShouldSkip = () => teleportFailed || ShouldExternalSkip(),
            OnEnter = () =>
            {
                ClearExpectedTravelLogout();
                onReady();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private void UpdateXagmanFrameworkTick()
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(xagmanExpectedTravelLogoutContext)
            && IsXagmanExpectedTravelLogoutWindowActive()
            && plugin.IpcClient.LifestreamIsBusy())
        {
            xagmanExpectedTravelLogoutSawBusy = true;
        }
        MeasureFrameworkUpdateStep("Xagman.ProcessPendingMatchSelection", ProcessXagmanPendingMatchSelection);
        MeasureFrameworkUpdateStep("Xagman.UpdateTradeCapacityForecast", UpdateXagmanTradeCapacityForecast);
        if (xagmanRunning && plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            // Outside Network Helper has no peer network; it drives its own self-hosted state
            // machine and never publishes presence.
            if (!plugin.TaskRunner.IsRunning)
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
                MeasureFrameworkUpdateStep("Xagman.ResumeStandbyOwnerOnTonyCall", () => TryResumeXagmanOwnerStandbyFromCallingTony());
        }
        var publishInterval = xagmanRunning ? 1.0 : 5.0;
        if (!plugin.Configuration.XagmanOutsideNetworkHelper
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
        return priorTonyPeers.All(peer => !peer.ActiveTradePartner.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
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

        var meetWorld = GetXagmanActiveMeetWorld();
        var meetAetheryte = GetXagmanActiveMeetAetheryte();
        if (string.IsNullOrWhiteSpace(meetWorld))
            return false;
        if (!TryValidateXagmanMeetTravel(xagmanActiveCharacter, meetWorld, out var routeFailure))
        {
            ReportXagmanTravelRouteError(
                $"Tony runtime rejected {xagmanActiveCharacter} -> {GetXagmanActiveMeetDestinationLabel()}: {routeFailure}");
            return true;
        }

        xagmanStatus = XagmanStatus.Traveling;
        if (plugin.IpcClient.LifestreamIsBusy())
        {
            MarkXagmanExpectedTravelLogoutBusyObserved();
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is still traveling to {GetXagmanActiveMeetDestinationLabel()}.";
            return true;
        }

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            xagmanStatusText =
                $"Tony {xagmanActiveCharacter} is still loading, casting, or zoning to {GetXagmanActiveMeetDestinationLabel()}; retry count is paused.";
            return true;
        }

        if (IsXagmanAtMeetDestination(meetWorld, meetAetheryte))
        {
            ClearXagmanExpectedTravelLogoutWindow();
            ResetXagmanTonyMeetRetryState();
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
                xagmanStatusText = xagmanTonyMeetCommandPhase == XagmanTonyMeetCommandPhase.AwaitingTargetWorld
                    ? $"Tony {xagmanActiveCharacter}'s accepted meetup command is still reaching {meetWorld}; retries are held."
                    : $"Tony {xagmanActiveCharacter}'s accepted meetup command is finishing the local teleport to {GetXagmanActiveMeetDestinationLabel()}; retries are held.";
                return true;
            }

            plugin.TaskRunner.AddLog(
                $"Xagman: Tony {xagmanActiveCharacter}'s accepted meetup command did not reach {GetXagmanActiveMeetDestinationLabel()} within its bounded {xagmanTonyMeetCommandPhase} window; retry evaluation may continue.");
            xagmanTonyMeetCommandPhase = XagmanTonyMeetCommandPhase.None;
            xagmanTonyMeetCommandDeadlineUtc = DateTime.MinValue;
        }

        if ((now - xagmanTonyLastMeetRetryUtc).TotalSeconds < retryCooldownSeconds)
        {
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is retrying meetup travel to {GetXagmanActiveMeetDestinationLabel()}.";
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
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        xagmanTonyLastMeetRetryUtc = now;
        xagmanTonyMeetRetryCount++;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} is retrying meetup travel to {GetXagmanActiveMeetDestinationLabel()} ({xagmanTonyMeetRetryCount}/{maxMeetRetries}).";
        plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} is not at the meet spot; retrying Lifestream command ({xagmanTonyMeetRetryCount}/{maxMeetRetries}).");
        var commandAccepted = ExecuteXagmanTravelCommandWithExpectedLogout(
            destinationCommand,
            "Tony meetup runtime reassert");
        if (!commandAccepted)
        {
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
        if (xagmanStatus == XagmanStatus.Error)
            return;
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
            xagmanStatusText = string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                ? $"Tony {xagmanActiveCharacter} is in a Dropbox trade."
                : $"Tony {xagmanActiveCharacter} is trading with {xagmanActiveTradePartner}.";
            return;
        }
        if (xagmanObservedDropboxBusy)
        {
            xagmanObservedDropboxBusy = false;
            if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            {
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"completed Tony receive from {xagmanActiveTradePartner}"))
                    return;
                ClearXagmanDropbox();
                plugin.TaskRunner.AddLog($"Xagman: Dropbox trade queue ended for {xagmanActiveTradePartner}; waiting for owner inventory reconciliation before treating the handoff as complete.");
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
            }
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is ready for the next owner.";
        }
        var ownerStandbyRotationReady = TryObserveXagmanOwnerStandbyRotationRequest();
        if (ownerStandbyRotationReady == false)
            return;
        if (TryRotateXagmanTonyForPendingOwnerStandbyRequest())
            return;
        if (TryReleaseXagmanTonyStalePartner())
        {
            if (!xagmanRunning)
                return;
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is ready for the next owner.";
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
            if (plugin.Configuration.XagmanSellWhenInventoryFull)
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
                    xagmanStatusText = $"Collection phase refused unexpected supply requests from {xagmanActiveTradePartner}.";
                    plugin.TaskRunner.AddLog(
                        $"Xagman: protocol/state error - owner {xagmanActiveTradePartner} advertised Tony supply requests during collection; requests were not dispatched.");
                    PublishXagmanPresence();
                    return;
                }
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"Tony {xagmanActiveCharacter} is resupplying {xagmanActiveTradePartner}.";
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
            xagmanStatusText = $"Tony {xagmanActiveCharacter} has called {xagmanActiveTradePartner}.";
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
                    xagmanStatusText = $"Collection phase refused unexpected supply requests from {inFlightOwner.ActiveCharacter}.";
                    plugin.TaskRunner.AddLog(
                        $"Xagman: protocol/state error - owner {inFlightOwner.ActiveCharacter} advertised Tony supply requests during collection; requests were not dispatched.");
                    PublishXagmanPresence();
                    return;
                }
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"Tony {xagmanActiveCharacter} is resupplying {inFlightOwner.ActiveCharacter}.";
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
                if (!TryRequireXagmanReceiverAutoAccept($"Tony receiving from {inFlightOwner.ActiveCharacter}"))
                    return;
            }
            xagmanStatus = inFlightOwner.Status == XagmanStatus.Trading ? XagmanStatus.Trading : XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting for {inFlightOwner.ActiveCharacter} to finish the active trade handoff.";
            return;
        }
        if (queue.Count == 0)
        {
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
                        xagmanStatusText = $"Tony {xagmanActiveCharacter} finished server {xagmanSweepDataCenter}; confirming before advancing.";
                        return;
                    case XagmanSweepStep.WaitingForOwners:
                        xagmanStatus = XagmanStatus.AtMeetSpot;
                        xagmanStatusText = $"Tony {xagmanActiveCharacter} is at {xagmanSweepDataCenter} ({GetXagmanTonySweepMeetWorld()}); waiting for owners.";
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
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting at the meet spot.";
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
            xagmanStatusText = $"Collection phase refused unexpected supply requests from {next.ActiveCharacter}.";
            plugin.TaskRunner.AddLog(
                $"Xagman: protocol/state error - owner {next.ActiveCharacter} entered the collection queue with Tony supply requests; requests were not dispatched.");
            PublishXagmanPresence();
            return;
        }
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} sees {queue.Count} owner(s) in queue.";
        if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
            return;
        xagmanActiveTradePartner = next.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = next.InstanceId;
        if (!hasRequestedItems)
        {
            if (!TryRequireXagmanReceiverAutoAccept($"Tony receiving from {next.ActiveCharacter}"))
                return;
        }
        xagmanStatus = XagmanStatus.Called;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} called {next.ActiveCharacter}.";
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
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"stale owner release {activePartner}"))
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
        if (IsXagmanCollectionFirstCollectionPhase())
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
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var tonyCharacter = xagmanActiveCharacter;
        var coordinatedCompletion = broadcastPeerCompletion && IsXagmanCollectionFirstRestockPhase();
        System.Threading.Tasks.Task<bool>? peerCompletionTask = null;
        var peerCompletionDeliveryFailed = false;
        var steps = new List<TaskStep>();

        void MarkPeerCompletionDeliveryFailed(string message)
        {
            if (peerCompletionDeliveryFailed || !coordinatedCompletion)
                return;

            peerCompletionDeliveryFailed = true;
            SetXagmanRunning(true);
            xagmanActiveRole = XagmanRole.Tony;
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = message;
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
                    "Final cleanup is paused because one or more frozen FO clients did not acknowledge the scoped completion command. Tony remains connected; inspect/reconnect clients, then explicitly stop or retry cleanup.");
            }
            return true;
        }

        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Summary: {tonyCharacter}",
            OnEnter = () =>
            {
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony completion {tonyCharacter}"))
                    return;
                ClearXagmanDropbox();
                if (broadcastPeerCompletion)
                    peerCompletionTask ??= CompleteAllXagmanPeersAsync();
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
                xagmanStatusText = completedWithWarnings
                    ? $"Tony {tonyCharacter} completed with unresolved work."
                    : autoDetectedNoRemainingOwners
                        ? $"Tony {tonyCharacter} completed after all Franchise Owners finished."
                        : (string.IsNullOrWhiteSpace(requestedBy)
                            ? $"Tony {tonyCharacter} completed."
                            : $"Tony {tonyCharacter} completed after {requestedBy} finished.");
                runner.AddLog(completedWithWarnings
                    ? $"Xagman: Tony {tonyCharacter} is completing with warning summary after Tony capacity exhaustion."
                    : autoDetectedNoRemainingOwners
                        ? $"Xagman: Tony {tonyCharacter} detected 0 remaining Franchise Owners and is completing."
                        : (string.IsNullOrWhiteSpace(requestedBy)
                            ? $"Xagman: Tony {tonyCharacter} received completion signal."
                            : $"Xagman: Tony {tonyCharacter} received completion signal from {requestedBy}.")); 
                if (completedWithWarnings)
                {
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
                "FC",
                () => "fc",
                runner,
                null,
                true,
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Returning Tony {tonyCharacter} to FC.";
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Tony {tonyCharacter} return-to-FC attempt finished.";
                    RememberXagmanRecentFcReturn(tonyCharacter);
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = $"Tony {tonyCharacter} return-to-FC attempt failed to start cleanly.";
                },
                expectCrossDataCenterLogout: true);
        }
        if (broadcastPeerCompletion)
        {
            steps.Add(new TaskStep
            {
                Name = "Xagman Tony Wait For Peer Completion",
                OnEnter = () => peerCompletionTask ??= CompleteAllXagmanPeersAsync(),
                IsComplete = PollPeerCompletionDelivery,
                TimeoutSec = 40f,
                OnTimeout = () => MarkPeerCompletionDeliveryFailed(
                    "Final cleanup is paused because the scoped FO completion acknowledgement window timed out. Tony remains connected; inspect/reconnect clients, then explicitly stop or retry cleanup."),
            });
        }
        steps.Add(new TaskStep
        {
            Name = "Xagman Tony Disconnect Peer Service",
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
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete));
        steps.Add(new TaskStep
        {
            Name = "Xagman Clear Failed Completion Logout Window",
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
            "Xagman",
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
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void HandleXagmanTonySupplyDepletion(IReadOnlyList<XagmanTradeRequestEntry> requestedItems, string partnerName)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;

        var requestedEntries = requestedItems.Count(IsValidXagmanTradeRequest);
        var requestedUnits = requestedItems
            .Where(IsValidXagmanTradeRequest)
            .Sum(entry => Math.Max(0, entry.Quantity));
        var activeTony = xagmanActiveCharacter;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony supply depletion for {partnerName}"))
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
        void AbortTonyTrade(string message, bool cleanupTradeWindow)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = message;
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
            if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony trade failure with {partnerName}"))
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
                $"Green-value supply for {partnerName} refused because {peerValidationFailure}.";
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
            xagmanStatusText = $"Green-value supply for {partnerName} failed closed: {xagmanGreenSupplyValidationError}";
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
            xagmanStatusText = $"Waiting for {partnerName} to reach Tony {xagmanActiveCharacter}.";
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
                    $"Green-value supply stopped because {peerFailure}.",
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
                    if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony receive completion for {partnerName}"))
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
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony supply completion for {partnerName}"))
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
                Name = $"Xagman Wait For {partnerName}",
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Waiting for {partnerName} to reach Tony {xagmanActiveCharacter}.";
                },
                IsComplete = () => IsCharacterInRangeWithoutMoving(partnerName),
                TimeoutSec = 60f,
                OnTimeout = () => AbortTonyTrade($"{partnerName} did not reach Tony {xagmanActiveCharacter}.", false),
            },
            new()
            {
                Name = $"Xagman Tony Trade Open Dropbox {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = $"Trading with {partnerName}.";
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Open Item Tab {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Open Item Tab Wait: {partnerName}", 1.0f, ShouldSkipTonyTradeFlow),
            new()
            {
                Name = $"Xagman Tony Trade Clear Queue {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    if (usingGreenValueRequests && !TryClearXagmanDropbox(out var failure))
                    {
                        AbortTonyTrade(
                            $"Green-value supply refused because Dropbox queue clear could not be confirmed: {failure}",
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
                Name = $"Xagman Tony Trade Queue Items {partnerName}",
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
                            $"Green-value supply refused before queueing because {peerFailure}.",
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
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Queue Wait {partnerName}", 0.5f, ShouldSkipTonyTradeFlow),
            new()
            {
                Name = $"Xagman Tony Trade Retarget {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => TryTargetCharacter(GetCurrentPartnerVisibleName()),
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Retarget Wait {partnerName}", 0.1f, ShouldSkipTonyTradeFlow),
            new()
            {
                Name = $"Xagman Tony Trade Focus Target {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    TryTargetCharacter(GetCurrentPartnerVisibleName());
                    FocusXagmanCurrentTarget(GetCurrentPartnerVisibleName());
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Focus Wait {partnerName}", 0.15f, ShouldSkipTonyTradeFlow),
            new()
            {
                Name = $"Xagman Tony Trade {partnerName}: Disable Auto-Accept Trades",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony trade start with {partnerName}"),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Start {partnerName}",
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
                            $"Green-value supply refused before Dropbox start because {peerFailure}.",
                            true);
                        return;
                    }
                    if (!StartXagmanDropboxTrade($"Tony trade with {partnerName}"))
                    {
                        AbortTonyTrade($"Failed to start Dropbox trading queue with {partnerName}.", true);
                        return;
                    }
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Wait {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => xagmanObservedDropboxBusy = false,
                IsComplete = PollTonyTradeWait,
                TimeoutSec = 600f,
                OnTimeout = () => AbortTonyTrade($"Trade timed out with {partnerName}.", true),
            },
            new()
            {
                Name = $"Xagman Tony Trade Finish {partnerName}",
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
                    xagmanStatusText = $"Tony {xagmanActiveCharacter} finished trading with {partnerName}.";
                    xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                Name = $"Xagman Tony Trade {partnerName}: Disable Auto-Accept Trades",
                OnEnter = () => TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony trade completion with {partnerName}"),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
        };
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true);
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

        var message = $"Owner {partnerName} did not reach Tony {xagmanActiveCharacter} within {XagmanTonyApproachWaitTimeoutSeconds:0} seconds; releasing the call with an error.";
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = message;
        plugin.TaskRunner.AddLog($"Xagman: {message}");
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"stale owner release {partnerName}"))
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
        out string failure)
    {
        livePeer = GetXagmanActiveTradeOwnerPeer();
        if (livePeer == null)
        {
            failure = "the active Franchise Owner peer is missing, stale, stopped, or in another run phase";
            return false;
        }
        if (livePeer.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
        {
            failure =
                $"the active Franchise Owner no longer advertises green-value protocol {XagmanGreenValueProtocolRevision}";
            return false;
        }

        if (!Enum.IsDefined(livePeer.Status))
        {
            failure = $"the active Franchise Owner published unknown status {(int)livePeer.Status}";
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
                    "the active Franchise Owner is logged out or its live character identity no longer matches the trade partner";
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
                failure = $"the active Franchise Owner is in incompatible active status {livePeer.Status}";
                return false;
            }
        }
        else if (livePeer.Status is XagmanStatus.Error or XagmanStatus.Idle)
        {
            failure = $"the active Franchise Owner is in incompatible status {livePeer.Status}";
            return false;
        }

        if (liveRequests.Any(entry => entry == null || !IsValidXagmanTradeRequest(entry)))
        {
            failure = "the active Franchise Owner published a malformed request payload";
            return false;
        }
        if (HasDuplicateXagmanExactTradeRequests(liveRequests))
        {
            failure = "the active Franchise Owner published duplicate exact Item ID/HQ requests";
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
                        "the active Franchise Owner added or expanded an exact request during the captured trade";
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
            failure = "the active Franchise Owner published duplicate green-value selectors";
            return false;
        }
        foreach (var liveGreenRequest in liveGreenRequests)
        {
            if (!TryValidateXagmanGreenTradeRequest(liveGreenRequest, out failure))
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
                    "the active Franchise Owner did not publish a complete value snapshot for the captured green target";
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
                        "the active Franchise Owner green request no longer matches its value snapshot or captured target";
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
                        "the active Franchise Owner removed a captured green request before its value snapshot proved the target was reached";
                    return false;
                }
            }
        }

        if (requireMatchingRequests
            && !AreXagmanTradeRequestListsEquivalent(capturedRequests, liveRequests))
        {
            failure = "the active Franchise Owner request changed before Dropbox trade start";
            return false;
        }

        failure = string.Empty;
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
                    $"Server Matching cannot rotate from region {xagmanSweepRegion}: no selected Tony remains in that region or any later required region.");
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
                    $"Server Matching cannot leave region {xagmanSweepRegion} during the coordinated {xagmanRunPhase} pass after its last Tony failed or was rotated.");
                return;
            }

            if (TryAdvanceXagmanSweepToNextNeededRegion(
                    "has no same-region replacement Tony; advancing explicitly without reusing its destination"))
            {
                return;
            }

            ReportXagmanTravelRouteError(
                $"Server Matching has no same-region replacement Tony for {xagmanSweepRegion} and no reachable later region to advance to.");
            return;
        }
        if (xagmanServerMatchingActive
            && !TryValidateXagmanServerMatchingTonyRoute(
                nextKey,
                xagmanSweepRegion,
                xagmanSweepDataCenter,
                out var routeFailure))
        {
            ReportXagmanTravelRouteError($"Server Matching rotation rejected {nextKey}: {routeFailure}");
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
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony rotation to {nextEntry.CharacterNameWorld}"))
            return;
        StartXagmanTonyStartup(nextEntry, true);
    }

    private static bool TryValidateXagmanMeetTravel(
        string characterNameWorld,
        string destinationWorld,
        out string reason)
    {
        var sourceHomeWorld = GetWorldFromKey(characterNameWorld);
        var reachability = WorldData.GetTravelReachability(sourceHomeWorld, destinationWorld);
        if (reachability == WorldData.WorldTravelReachability.Reachable)
        {
            reason = string.Empty;
            return true;
        }

        reason = reachability switch
        {
            WorldData.WorldTravelReachability.UnknownSourceWorld =>
                $"home world '{sourceHomeWorld}' is blank or unknown",
            WorldData.WorldTravelReachability.UnknownDestinationWorld =>
                $"destination world '{destinationWorld}' is blank or unknown",
            _ =>
                $"home region {WorldData.GetByName(sourceHomeWorld)?.Region ?? "Unknown"} cannot travel to "
                + $"destination region {WorldData.GetByName(destinationWorld)?.Region ?? "Unknown"}",
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
        return TryValidateXagmanMeetTravel(routeCharacter, peer.MeetWorld, out _);
    }

    private bool IsXagmanOwnerPeerReachableForActiveTony(XagmanPeerPresence peer)
    {
        return peer.Status != XagmanStatus.Error
            && HasCompleteXagmanActiveMeetDestination()
            && TryValidateXagmanMeetTravel(peer.ActiveCharacter, xagmanActiveMeetWorld, out _);
    }

    private void ReportXagmanTravelRouteError(string reason)
    {
        ClearXagmanExpectedTravelLogoutWindow();
        ResetXagmanTonyMeetRetryState();
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        xagmanPhaseComplete = false;
        if (IsXagmanCollectionFirstRunActive())
            xagmanTravelRouteFatalError = true;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = $"Xagman travel route error: {reason}";
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
        xagmanStatusText = $"Owner {ownerCharacter} is moving near {locationLabel}.";
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
        var unreachablePeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => !peer.ServerMatchingEnabled)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status != XagmanStatus.Error)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
            .Where(peer => HasCompleteXagmanMeetDestination(peer.MeetWorld, peer.MeetAetheryte))
            .FirstOrDefault(peer =>
                !TryValidateXagmanMeetTravel(ownerCharacter, peer.MeetWorld, out _));
        if (unreachablePeer == null)
        {
            reason = string.Empty;
            return false;
        }

        TryValidateXagmanMeetTravel(ownerCharacter, unreachablePeer.MeetWorld, out var routeFailure);
        reason =
            $"{unreachablePeer.ActiveCharacter} advertised {unreachablePeer.MeetWorld}, but {routeFailure}";
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
            return TryGetXagmanFixedMeetRouteFailureForOwner(routeOwner, out _);
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
        return xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && xagmanOwnerStandbyPending
            && xagmanOwnerPauseForTonyRotationRequested
            && !plugin.TaskRunner.IsRunning;
    }

    private string GetXagmanPriorityStatusLabel()
    {
        const string baseLabel = "Xagman";
        if (!xagmanRunning)
            return baseLabel;

        if (xagmanActiveRole == XagmanRole.Tony)
        {
            var total = GetXagmanLocalTonyTotalCharacters();
            if (total <= 0)
                return baseLabel;
            return $"{baseLabel}: {Math.Max(1, GetXagmanLocalTonyCurrentCharacterNumber())}/{total}";
        }

        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            var total = GetXagmanLocalOwnerTotalCharacters();
            if (total <= 0)
                return baseLabel;
            var current = xagmanStatus == XagmanStatus.Completed
                ? total
                : Math.Min(total, GetXagmanLocalOwnerCompletedCharacters() + 1);
            return $"{baseLabel}: {Math.Max(1, current)}/{total}";
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
        if (plugin.Configuration.XagmanTonyCharacters.Count > 0)
            return plugin.Configuration.XagmanTonyCharacters[0].CharacterNameWorld;
        return string.Empty;
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
        xagmanPreferredTonyCharacter = tonyPeer.ActiveCharacter;
        xagmanActiveTradePartner = tonyPeer.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = tonyPeer.InstanceId;
        xagmanTonyMode = tonyPeer.TonyMode;
        if (changed
            && ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(characterNameWorld)
            && !TryRequireXagmanReceiverAutoAccept($"owner {characterNameWorld} pending Tony supply"))
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
            && (IsXagmanCollectionFirstRestockPhase()
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
        string label,
        bool enabled,
        Func<bool>? shouldSkip = null,
        bool requireSuccess = false,
        System.Action? onFailure = null)
     {
         steps.Add(new TaskStep
         {
             Name = $"{label}: {(enabled ? "Enable" : "Disable")} Auto-Accept Trades",
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

     private bool StartXagmanDropboxTrade(string contextLabel)
     {
         if (plugin.IpcClient.DropboxBeginTrading())
             return true;

         plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(contextLabel)
             ? "Xagman: failed to start the Dropbox trading queue."
             : $"Xagman: failed to start the Dropbox trading queue for {contextLabel}.");
         return false;
     }

     private void CleanupXagmanDropboxTradeAttempt(string contextLabel)
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
        return ownerPeer.QueueRequestedAtUtc <= DateTime.MinValue || string.IsNullOrWhiteSpace(ownerKey)
            ? string.Empty
            : $"{ownerKey}|{ownerPeer.QueueRequestedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
    }

     private bool? TryObserveXagmanOwnerStandbyRotationRequest()
    {
        var ownerRotationPeer = GetXagmanOwnerRequestedTonyRotationPeer();
        if (ownerRotationPeer == null)
            return null;

        var ownerRotationRequestKey = GetXagmanOwnerStandbyRotationRequestKey(ownerRotationPeer);
        if (!string.IsNullOrWhiteSpace(ownerRotationRequestKey)
            && ownerRotationRequestKey.Equals(xagmanLastConsumedOwnerStandbyRotationRequestKey, StringComparison.Ordinal))
            return null;

        if (!ownerRotationPeer.TonyRotationReady)
        {
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting for {ownerRotationPeer.ActiveCharacter} to finish cancelling before rotation.";
            return false;
        }

        if (!xagmanTonyRotationRequestedByOwnerStandby)
            plugin.TaskRunner.AddLog($"Xagman: owner {ownerRotationPeer.ActiveCharacter} confirmed Tony rotation is safe after standby cancel.");
        if (!string.IsNullOrWhiteSpace(ownerRotationRequestKey))
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
        if (xagmanExpectedLogout)
        {
            var xagmanTaskRunnerWasActive = IsXagmanTaskRunnerActive();
            xagmanExpectedLogout = false;
            plugin.TaskRunner.AddLog("Xagman: expected relog/completion logout observed.");
            return xagmanTaskRunnerWasActive || !plugin.TaskRunner.IsRunning;
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
            xagmanStatusText = "Xagman stopped after an unexpected logout.";
        }
        return xagmanTaskRunnerWasRunning || !plugin.TaskRunner.IsRunning;
    }

     private bool TryBeginXagmanTradeSafetySession(string contextLabel)
    {
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

            return TrySetXagmanDropboxAutoAcceptOrStop(false, $"{contextLabel} start boundary");
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
        string contextLabel,
        System.Action? beforeStop = null)
    {
        if (TrySetXagmanDropboxAutoAccept(enabled))
            return true;

        var transitionLabel = enabled ? "on" : "off";
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
        xagmanStatusText = !safelyClosed
            ? $"Xagman stopped after the Dropbox auto-accept {transitionLabel} transition failed for {contextLabel}; Dropbox state remains unknown and trade refusal is suppressed."
            : stopCleanupRestoredRefusal
                ? $"Xagman stopped after the Dropbox auto-accept {transitionLabel} transition failed for {contextLabel}; stop cleanup confirmed auto-accept off and restored the saved manual refusal preference."
                : $"Xagman stopped after the Dropbox auto-accept {transitionLabel} transition failed for {contextLabel}; stop cleanup confirmed auto-accept off, but the requested manual refusal state could not be restored.";
        return false;
    }

     private bool TryRequireXagmanReceiverAutoAccept(string contextLabel)
    {
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
                    var scanError = greenScan?.Snapshot.Error;
                    if (string.IsNullOrWhiteSpace(scanError))
                        scanError = "The live green-item inventory scan is incomplete.";
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
                        GreenScanError = scanError,
                    });
                    var failureText = $"Green-value scan failed closed for {ownerCharacter}: {scanError}";
                    if (!xagmanStatusText.Equals(failureText, StringComparison.Ordinal))
                        plugin.TaskRunner.AddLog($"Xagman: {failureText}");
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = failureText;
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
                if (IsXagmanGilItem(item.ItemId))
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
            if (IsXagmanGilItem(item.ItemId))
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
        var effectiveItems = ResolveXagmanItemsForOwner(items, ownerCharacter);
        foreach (var item in effectiveItems)
        {
            if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            var pendingQuantity = 0;
            if (item.Mode == XagmanItemMode.Give)
            {
                if (item.Quantity <= 0)
                {
                    pendingQuantity = currentQuantity;
                }
                else
                {
                    var snapshotKey = GetXagmanTradeSnapshotKey(item.ItemId, item.IsHq);
                    if (xagmanTradeQuantitySnapshot.TryGetValue(snapshotKey, out var capturedQuantity))
                    {
                        var expectedRemaining = Math.Max(0, capturedQuantity - Math.Max(0, item.Quantity));
                        pendingQuantity = Math.Max(0, currentQuantity - expectedRemaining);
                    }
                    else
                    {
                        pendingQuantity = Math.Min(currentQuantity, item.Quantity);
                    }
                }
            }
            else if (item.Mode == XagmanItemMode.Balance)
            {
                pendingQuantity = Math.Max(0, currentQuantity - Math.Max(0, item.Quantity));
            }
            if (pendingQuantity > 0)
                return true;
        }
        return false;
     }

     private List<XagmanTradeRequestEntry> BuildXagmanTonySupplyRequests(IEnumerable<XagmanTradeRequestEntry> requests)
    {
        xagmanGreenSupplyValidationError = string.Empty;
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var requestList = requests?.ToList() ?? new List<XagmanTradeRequestEntry>();
        if (requestList.Any(entry => entry == null || !IsValidXagmanTradeRequest(entry)))
        {
            xagmanGreenSupplyValidationError =
                "Owner request payload contains an unsupported or malformed selector, mode, or item.";
            return new List<XagmanTradeRequestEntry>();
        }
        if (HasDuplicateXagmanExactTradeRequests(requestList))
        {
            xagmanGreenSupplyValidationError =
                "Owner request payload contains duplicate exact Item ID/HQ entries.";
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
                "Owner request payload contains duplicate green-value selectors.";
            return supplyRequests;
        }
        foreach (var greenRequest in greenRequests)
        {
            if (!TryValidateXagmanGreenTradeRequest(greenRequest, out var validationError))
            {
                xagmanGreenSupplyValidationError = validationError;
                return supplyRequests;
            }
        }
        if (!TryGetXagmanGreenValueScan(true, out var localGreenScan))
        {
            xagmanGreenSupplyValidationError = string.IsNullOrWhiteSpace(localGreenScan.Snapshot.Error)
                ? "Tony green-value scan is incomplete."
                : $"Tony green-value scan is incomplete: {localGreenScan.Snapshot.Error}";
            return supplyRequests;
        }
        if (localGreenScan.Snapshot.DropboxSafeItemCount <= 0
            && supplyRequests.Count == 0)
            return supplyRequests;
        if (!TryPlanXagmanGreenSupply(
                greenRequests,
                supplyRequests,
                out _,
                out var plannerError))
        {
            xagmanGreenSupplyValidationError = plannerError;
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
        error = string.Empty;
        if (!IsValidXagmanTradeRequest(request)
            || !request.ItemName.Equals(
                GetXagmanGreenValueSelectorName(request.SelectorKind),
                StringComparison.Ordinal))
        {
            error = "Owner green-value request has an invalid selector shape.";
            return false;
        }
        if (request.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
        {
            error =
                $"Owner request uses unsupported green-value protocol {request.GreenValueProtocolRevision}.";
            return false;
        }
        if (!request.GreenScanComplete)
        {
            error = string.IsNullOrWhiteSpace(request.GreenScanError)
                ? "Owner green-value scan is incomplete."
                : $"Owner green-value scan is incomplete: {request.GreenScanError}";
            return false;
        }
        if (request.TargetQuantity <= 0
            || request.TargetValueScaled2 != (long)request.TargetQuantity * 2L
            || request.CurrentQuantity < 0
            || request.CurrentValueScaled2 < 0)
        {
            error = "Owner green-value request contains inconsistent target or current values.";
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
            error = "Owner green-value request contains an inconsistent deficit.";
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
                    GreenScanError = "Malformed null request entry.",
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
                GreenScanError = entry.GreenScanError,
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
        var effectiveItems = ResolveXagmanItemsForOwner(items, ownerCharacter);
        foreach (var item in effectiveItems)
        {
            if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            var snapshotKey = GetXagmanTradeSnapshotKey(item.ItemId, item.IsHq);
            var startingQuantity = xagmanTradeQuantitySnapshot.TryGetValue(snapshotKey, out var capturedQuantity)
                ? capturedQuantity
                : currentQuantity;
            var expectedRemaining = item.Mode switch
            {
                XagmanItemMode.Give => item.Quantity <= 0
                    ? 0
                    : Math.Max(0, startingQuantity - Math.Max(0, item.Quantity)),
                XagmanItemMode.Balance => Math.Min(startingQuantity, Math.Max(0, item.Quantity)),
                _ => currentQuantity,
            };
            if (currentQuantity > expectedRemaining)
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
                            ? "Green-value reconciliation scan is incomplete."
                            : greenScan.Snapshot.Error;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = $"Green-value reconciliation failed closed for {ownerCharacter}: {error}";
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
        if (plugin.DropboxQueue.TryEnqueueXagmanItem(itemId, isHq, quantity, out var message))
            return true;

        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = $"Dropbox queue failed closed for {context}: {message}";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        ClearXagmanDropbox();
        return false;
    }

     private int QueueXagmanOwnerCollectionItems(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
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
            var quantity = item.Mode switch
            {
                XagmanItemMode.Give => item.Quantity <= 0 ? localAvailable : Math.Min(localAvailable, item.Quantity),
                XagmanItemMode.Balance => Math.Max(0, localAvailable - Math.Max(0, item.Quantity)),
                _ => 0,
            };
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
            && !TryPlanXagmanGreenSupply(
                greenRequests,
                exactRequests,
                out greenQueue,
                out var plannerError))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = $"Green-value supply queue failed closed: {plannerError}";
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
                return (int)inventoryManager->GetGil();

            var quantity = 0;
            var loadedContainerCount = 0;
            var inventoryTypes = IsXagmanElementalCrystalItem(itemId)
                ? XagmanCrystalInventoryTypes
                : XagmanMainInventoryTypes;
            foreach (var inventoryType in inventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded)
                    continue;
                loadedContainerCount++;
                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null
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
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 0;
            return Math.Max(0, (int)inventoryManager->GetEmptySlotsInBag());
        }
        catch
        {
            return 0;
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
        xagmanStatusText = $"Owner {characterNameWorld} is on standby for the next Tony.";
        xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"owner standby for {characterNameWorld}"))
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

     private bool TryStartXagmanTonySellWhenInventoryFull(string activePartner)
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

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
        {
            plugin.TaskRunner.AddLog("Xagman: Sell When Inventory Is Full is enabled, but AutoRetainer IPC is not available; using normal Tony full-inventory behavior.");
            return false;
        }

        if (!plugin.IpcClient.VnavIsReady())
        {
            plugin.TaskRunner.AddLog("Xagman: Sell When Inventory Is Full is enabled, but vnavmesh is not ready; using normal Tony full-inventory behavior.");
            return false;
        }

        StartXagmanTonySellWhenInventoryFullTask(destination, activePartner, currentGil);
        return true;
    }

     private void StartXagmanTonySellWhenInventoryFullTask(XagmanTonySellDestination destination, string activePartner, int currentGil)
    {
        var runner = plugin.TaskRunner;
        var activeTony = xagmanActiveCharacter;
        var sellFailed = false;
        var sellSucceeded = false;
        var pathStarted = false;
        var arBusyObserved = false;
        var nextArBusyPollUtc = DateTime.MinValue;
        var destinationLabel = $"{destination.NpcName} at {destination.LocationName}";
        var randomizedDestinationPosition = RandomizeXagmanPosition(destination.Position, XagmanTonySellVendorRandomRadius);
        var sellFallbackReason = $"Xagman: Tony {activeTony} item-sell cleanup did not complete; using normal Tony full-inventory behavior.";

        void MarkSellFailed(string message, string? fallbackReasonOverride = null)
        {
            if (sellFailed)
                return;
            sellFailed = true;
            if (!string.IsNullOrWhiteSpace(fallbackReasonOverride))
                sellFallbackReason = fallbackReasonOverride;
            runner.AddLog(message);
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = $"Tony {activeTony} item-sell cleanup failed; falling back to normal full-inventory handling.";
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
                Name = $"Xagman Tony Sell Setup: {activeTony}",
                OnEnter = () =>
                {
                    if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony sell setup for {activeTony}"))
                        return;
                    ClearXagmanDropbox();
                    ClearXagmanFocusTarget();
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = $"Tony {activeTony} is pausing to sell full-inventory items.";
                    runner.AddLog($"Xagman: Tony {activeTony} inventory is full; Sell When Inventory Is Full is routing to randomized coords {FormatXagmanTonySellPosition(randomizedDestinationPosition)} within {XagmanTonySellVendorRandomRadius:0.###}y of {destinationLabel} ({destination.ZoneName}) with vnav stop distance {XagmanTonySellVendorStopDistance:0.###}.");
                    runner.AddLog($"Xagman: Tony {activeTony} gil before selling is {currentGil.ToString("N0", CultureInfo.InvariantCulture)}; selling is disabled at {XagmanTonySellGilLimit.ToString("N0", CultureInfo.InvariantCulture)} or above.");
                    PublishXagmanPresence();
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                Name = $"Xagman Tony Sell Path: {destinationLabel}",
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = $"Tony {activeTony} is pathing to {destinationLabel}.";
                    pathStarted = false;
                    if (!plugin.IpcClient.VnavPathfindAndMoveCloseTo(randomizedDestinationPosition, false, XagmanTonySellVendorStopDistance))
                        MarkSellFailed($"Xagman: vnavmesh did not accept the item-sell path to {destinationLabel}.");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                Name = $"Xagman Tony Sell Path Start: {destinationLabel}",
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
                Name = $"Xagman Tony Sell Arrive: {destinationLabel}",
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
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Sell Settle: {activeTony}", 0.25f, ShouldSkipSellStep),
            new()
            {
                Name = $"Xagman Tony Sell Command: {activeTony}",
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = $"Tony {activeTony} is running /ays itemsell.";
                    runner.AddLog($"Xagman: Tony {activeTony} reached {destinationLabel}; sending /ays itemsell.");
                    ChatHelper.SendMessage("/ays itemsell");
                    arBusyObserved = false;
                    nextArBusyPollUtc = DateTime.UtcNow.AddSeconds(1);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                Name = $"Xagman Tony Sell Wait AutoRetainer: {activeTony}",
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = $"Tony {activeTony} is waiting for AutoRetainer item selling.";
                },
                IsComplete = () =>
                {
                    if (DateTime.UtcNow < nextArBusyPollUtc)
                        return false;

                    var busy = plugin.IpcClient.AutoRetainerPluginStateIsBusy();
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

                    if (arBusyObserved)
                        runner.AddLog($"Xagman: AutoRetainer item selling finished for Tony {activeTony}.");
                    else
                        runner.AddLog($"Xagman: AutoRetainer did not report busy after /ays itemsell for Tony {activeTony}; continuing to CharacterSafeWait.");
                    return true;
                },
                TimeoutSec = 900f,
                OnTimeout = () => MarkSellFailed($"Xagman: timed out waiting for AutoRetainer item selling to finish for Tony {activeTony}."),
            },
        };

        foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Xagman Tony ItemSell SafeWait: {activeTony}", 30f))
        {
            var originalComplete = safeWait.IsComplete;
            steps.Add(new TaskStep
            {
                Name = safeWait.Name,
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
            Name = $"Xagman Tony Sell Resume: {activeTony}",
            ShouldSkip = ShouldSkipSellStep,
            OnEnter = () =>
            {
                sellSucceeded = true;
                SetXagmanTonySellLocation(destination, randomizedDestinationPosition);
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = $"Tony {activeTony} sold full-inventory items and is ready for the next owner.";
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                runner.AddLog($"Xagman: Tony {activeTony} finished item selling at {destinationLabel} and is resuming Xagman operations from that location.");
                PublishXagmanPresence();
                StartAllXagmanPeers();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        runner.Start(
            "Xagman",
            steps,
            onFinished: () =>
            {
                if (sellSucceeded)
                {
                    UpdateXagmanTonyTaskRunnerProgress();
                    return;
                }

                ScheduleXagmanTonyFullInventoryFallback(activePartner, sellFallbackReason);
            },
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"),
            suppressCompletionReport: true);
    }

     private void ScheduleXagmanTonyFullInventoryFallback(string activePartner, string fallbackReason)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
                await Plugin.Framework.Run(() =>
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
        ResetXagmanTonySellLocation();
        if (!string.IsNullOrWhiteSpace(fallbackReason))
            plugin.TaskRunner.AddLog(fallbackReason);

        var fallbackContext = string.IsNullOrWhiteSpace(activePartner)
            ? $"Xagman: Tony {xagmanActiveCharacter} hit a full-inventory/standby rotation."
            : $"Xagman: owner {activePartner} requested Tony rotation after trade failure.";
        if (TryRotateXagmanTonyForCapacityExhaustion(fallbackContext))
            return;

        if (IsXagmanCollectionFirstCollectionPhase())
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = "Collection pass stopped because every selected Tony is full or unavailable; restock was not started.";
            plugin.TaskRunner.AddLog(
                $"{fallbackContext} No alternate Tony remains. Collection-first fails closed here so FO clients stay paused and Tony never begins restock with an incomplete collection pool.");
            PublishXagmanPresence();
            return;
        }

        plugin.TaskRunner.AddLog($"{fallbackContext} No alternate Tony remains; finalizing with warning summary.");
        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: false, completedWithWarnings: true, broadcastPeerCompletion: true);
    }

     private bool TryRotateXagmanTonyForPendingOwnerStandbyRequest()
    {
        if (!xagmanTonyRotationRequestedByOwnerStandby || !xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return false;

        xagmanTonyRotationRequestedByOwnerStandby = false;
        var activePartner = xagmanActiveTradePartner;
        xagmanObservedDropboxBusy = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;

        if (TryStartXagmanTonySellWhenInventoryFull(activePartner))
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
