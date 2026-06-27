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
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;
namespace XASlave.Windows;
public partial class SlaveWindow
{
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
    private bool xagmanTonyOpportunisticSellArmed = true;
    private bool xagmanObservedDropboxBusy;
    private bool xagmanTonyObservedOwnerWork;
    private bool xagmanTonyRotationRequestedByOwnerStandby;
    private string xagmanLastConsumedOwnerStandbyRotationRequestKey = string.Empty;
    private bool xagmanOwnerStartRequested;
    private bool xagmanOwnerStandbyPending;
    private bool xagmanOwnerPauseForTonyRotationRequested;
    private bool xagmanTonySellLocationActive;
    private uint xagmanTonySellLocationTerritoryId;
    private string xagmanTonySellLocationName = string.Empty;
    private Vector3 xagmanTonySellLocationPosition;
    private readonly Dictionary<string, int> xagmanTradeQuantitySnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<XagmanTradeRequestEntry> xagmanOwnerRequestedItems = new();
    private DateTime xagmanQueueRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
    private DateTime xagmanLastPresencePublishUtc = DateTime.MinValue;
    private DateTime xagmanLastTonyActionAtUtc = DateTime.MinValue;
    private DateTime xagmanRecentFcReturnAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyRunStartedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyLastMeetRetryUtc = DateTime.MinValue;
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
    private List<string>? xagmanAetheryteNames;
    private string xagmanRecentFcReturnCharacter = string.Empty;
    private List<XagmanItemSearchEntry> xagmanItemResults = new();
    private Dictionary<string, XagmanItemSearchEntry>? xagmanItemNameLookupCache;
    private static readonly string[] xagmanItemModeLabels = { "Give", "Take", "Balance", "TopUp" };
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
        public bool IsHq { get; init; }
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
        public int SchemaVersion { get; set; } = 1;
        public string ListId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; }
        public List<XagmanItemEntry> Items { get; set; } = new();
    }
    private sealed class XagmanDbSearchMatch
    {
        public string CharacterNameWorld { get; init; } = string.Empty;
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
    private sealed class XagmanMatchQueryRequest
    {
        public string Query { get; init; } = string.Empty;
        public HashSet<string> ItemKeys { get; init; } = new(StringComparer.Ordinal);
    }
    private sealed class XagmanPendingMatchSelectionRequest
    {
        public XagmanMatchSelectionTarget Target { get; init; }
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
        if (ImGui.RadioButton("Tony##xagmanRoleTony", role == XagmanRole.Tony))
        {
            cfg.XagmanRole = XagmanRole.Tony;
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Franchise Owner##xagmanRoleOwner", role == XagmanRole.FranchiseOwner))
        {
            cfg.XagmanRole = XagmanRole.FranchiseOwner;
            cfg.Save();
        }
        ImGui.Spacing();
        if (ImGui.SmallButton($"{(cfg.XagmanRoleInstructionsExpanded ? "Setup Guide: Expanded" : "Setup Guide: Collapsed")}##xagmanSetupGuideToggle"))
        {
            cfg.XagmanRoleInstructionsExpanded = !cfg.XagmanRoleInstructionsExpanded;
            cfg.Save();
        }
        if (cfg.XagmanRoleInstructionsExpanded)
            DrawXagmanRoleInstructions(cfg.XagmanRole);
        ImGui.Spacing();
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
            PullXaDatabaseInfo();
            ClearXagmanMatchingSelectionCaches();
        }
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            DrawXagmanWorldSelector(cfg);
            DrawXagmanAetheryteSelector(cfg);
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
            if (!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld))
                ImGui.TextDisabled($"Meet Destination: {GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)}");
            else
                ImGui.TextDisabled("Meet Destination: not set");
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
        var hasMeetDestination = !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte);
        var canStartTony = !xagmanRunning && localXagmanPeerConnected && selectedTonyChars.Count > 0 && hasMeetDestination && allRequired;
        var canStartOwners = !xagmanRunning && localXagmanPeerConnected && selectedFranchiseChars.Count > 0 && allRequired;
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                $"Start Tony ({selectedTonyChars.Count})##xagmanTonyStart",
                canStartTony,
                StartXagmanTonyTask,
                !allRequired
                    ? "Missing required plugins. Check the plugin status above."
                    : !localXagmanPeerConnected
                        ? "Connect the local Xagman peer service first."
                    : !hasMeetDestination
                        ? "Select a meet world and aetheryte first."
                        : "Select at least one Tony character.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref xagmanShowLog);

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

            if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony && xagmanTonyRunList.Count > 1 && !runner.IsRunning)
            {
                ImGui.SameLine();
                if (ImGui.Button("Rotate Tony##xagmanRotateTony"))
                    RotateXagmanTony();
            }
        }
        else
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                $"Start Owners ({selectedFranchiseChars.Count})##xagmanOwnerStart",
                canStartOwners,
                () => StartXagmanFranchiseTask(),
                !allRequired
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
            DrawXagmanProcessingLists(runner);
            ImGui.Spacing();
        }
        ImGui.Spacing();
        if (cfg.XagmanRole == XagmanRole.Tony)
            DrawXagmanTonyTable(cfg);
        else
            DrawXagmanFranchiseTable(cfg);
        ImGui.Spacing();
        if (cfg.XagmanRole == XagmanRole.Tony)
            DrawXagmanItemSection("Tony Search Item List", cfg.XagmanTonyItems, "xagmanTonyItems", searchOnly: true, allowGil: false);
        else
            DrawXagmanItemSection("Shared Item List", cfg.XagmanItems, "xagmanItems");
        ImGui.Spacing();
        ImGui.Checkbox("Show Queue##xagmanQueue", ref xagmanShowQueue);
        if (xagmanShowQueue)
            DrawXagmanQueueTable();
        ImGui.Spacing();
        ImGui.Checkbox("Show Peers##xagmanPeers", ref xagmanShowPeers);
        if (xagmanShowPeers)
            DrawXagmanPeersTable();
        ImGui.Spacing();
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
        ImGui.TextWrapped("2. Click Pull XA Database Info after importing. This scans inventory data from XA Database so Select Matching Items can work.");
        ImGui.TextWrapped("3. Recommended: run Monthly Relogger with the full action sweep at least once to mass-collect the latest character information before relying on Xagman matching and inventory-driven routing.");
        ImGui.Spacing();
        ImGui.TextColored(infoColor, "Required checks before running");
        ImGui.TextWrapped("1. Lifestream must have registered plot info, and Settings > General > Enter House must be enabled when teleporting to your FC house so returns land correctly.");
        ImGui.TextWrapped("2. Dropbox must be installed and available. XA Slave now owns the `/xa db ...` queue commands directly.");
    }

    private void DrawXagmanRoleInstructions(XagmanRole role)
    {
        var sectionColor = new Vector4(0.4f, 0.8f, 1.0f, 1.0f);
        if (role == XagmanRole.Tony)
        {
            ImGui.TextColored(sectionColor, "Tony Setup");
            ImGui.TextWrapped("1. Optional: create a Tony Search Item List.");
            ImGui.TextWrapped("2. Filter region if needed or use the search bar. Select characters manually, or use Select Matching Items to select visible Tonys that hold items from the Tony Search Item List.");
            ImGui.TextWrapped("3. Select a world to meet and a meet location as the set aetheryte.");
            ImGui.TextWrapped("4. Set Tony Gil Minimum. Default: 10000.");
            ImGui.TextWrapped("5. Connect peers. Tony is then ready for Start Tony, or you can use Start All Peers / Stop All Peers when the alt clients are connected and selected.");
            ImGui.TextWrapped("6. Tony moves into position, confirms the meetup location, and then sends the green light for everyone else to start processing.");
            return;
        }

        ImGui.TextColored(sectionColor, "Franchise Owner Setup");
        ImGui.TextWrapped("1. Create a list of items in Shared Item List.");
        ImGui.Indent();
        ImGui.TextWrapped("- Balance: keep the owner at the selected amount by giving missing units and taking extras.");
        ImGui.TextWrapped("- Give: give a fixed amount or 0 for all.");
        ImGui.TextWrapped("- Take: take a fixed amount.");
        ImGui.TextWrapped("- TopUp: give the owner up to the selected amount and leave any extra untouched.");
        ImGui.Unindent();
        ImGui.TextWrapped("2. Filter region if needed or use the search bar. Select characters manually, or use Select Matching Items to select visible characters with matching items from the Shared Item List.");
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
            xagmanTonySelectedIndices.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Select Matching Items##xagmanTonyMatching"))
            SelectXagmanTonyCharactersWithMatchingItems();
        ImGui.SameLine();
        ImGui.Checkbox("Selected Only##xagmanTonySelOnly", ref xagmanTonyShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanTonySearch", "Search Tony name or world...", ref xagmanTonySearchFilter, 128);
        if (ImGui.BeginTable("XagmanTonyTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 175f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Inv", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
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
                }
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.TextUnformatted(displayName);
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
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##xagmanTonyRemove{i}"))
                {
                    chars.RemoveAt(i);
                    ReindexSelectionSet(xagmanTonySelectedIndices, i);
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
            xagmanFranchiseSelectedIndices.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Select Matching Items##xagmanOwnerMatching"))
            SelectXagmanFranchiseCharactersWithMatchingItems();
        ImGui.SameLine();
        ImGui.Checkbox("Selected Only##xagmanOwnerSelOnly", ref xagmanFranchiseShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanOwnerSearch", "Search owner name or world...", ref xagmanFranchiseSearchFilter, 128);
        if (ImGui.BeginTable("XagmanOwnerTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 175f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Inv", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
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
                }
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.TextUnformatted(displayName);
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
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##xagmanOwnerRemove{i}"))
                {
                    chars.RemoveAt(i);
                    ReindexSelectionSet(xagmanFranchiseSelectedIndices, i);
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
                cfg.Save();
            }
            xagmanFranchiseNewChar = string.Empty;
        }
    }
    private void DrawXagmanItemSection(string title, List<XagmanItemEntry> items, string id, bool searchOnly = false, bool allowGil = true)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), title);
        if (searchOnly)
            ImGui.TextDisabled("Search-only supplier list. Imported modes and quantities are ignored and the Tony table selects visible characters that hold any listed item.");
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button($"Add Item##{id}AddItem"))
            ImGui.OpenPopup($"{id}AddItemPopup");
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.BeginPopup($"{id}AddItemPopup"))
        {
            ImGui.SetNextItemWidth(Scale(280f));
            var searchChanged = ImGui.InputTextWithHint($"##{id}Search", "Search items...", ref xagmanItemSearch, 128);
            if (searchChanged || !string.Equals(xagmanItemQueryCache, xagmanItemSearch, StringComparison.Ordinal))
            {
                xagmanItemQueryCache = xagmanItemSearch;
                xagmanItemResults = SearchXagmanItems(xagmanItemSearch);
            }
            ImGui.Separator();
            if (xagmanItemResults.Count == 0)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(xagmanItemSearch) ? "Type to search..." : "No results.");
            }
            else
            {
                foreach (var result in xagmanItemResults)
                {
                    if (items.Any(entry => entry.ItemId == result.ItemId && entry.IsHq == result.IsHq))
                        continue;
                    var label = result.IsHq ? $"{result.ItemName} (HQ)##{id}{result.ItemId}" : $"{result.ItemName}##{id}{result.ItemId}";
                    if (ImGui.Selectable(label, false, ImGuiSelectableFlags.DontClosePopups))
                        AddXagmanItem(items, result.ItemId, result.ItemName, result.IsHq);
                }
            }
            ImGui.EndPopup();
        }

        if (allowGil)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Gil##{id}Gil"))
                AddXagmanItem(items, 1, "Gil", false);
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

        DrawXagmanSavedListsPopup(title, items, id, searchOnly);
        DrawXagmanExportPopup(title, items, id, searchOnly);
        if (!searchOnly)
            DrawXagmanMassModePopup(items, id);

        var tableColumnCount = searchOnly ? 4 : 6;
        if (ImGui.BeginTable($"{id}Table", tableColumnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, ScaledVector(0f, 150f)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            if (!searchOnly)
            {
                ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
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
                ImGui.TextDisabled(item.ItemId.ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                var isHq = item.IsHq;
                if (ImGui.Checkbox($"##{id}Hq{i}", ref isHq))
                {
                    item.IsHq = isHq;
                    SaveXagmanSharedItemsState();
                }
                if (!searchOnly)
                {
                    ImGui.TableNextColumn();
                    var modeIndex = (int)item.Mode;
                    ImGui.SetNextItemWidth(Scale(80f));
                    if (ImGui.Combo($"##{id}Mode{i}", ref modeIndex, xagmanItemModeLabels, xagmanItemModeLabels.Length))
                    {
                        item.Mode = (XagmanItemMode)Math.Clamp(modeIndex, 0, xagmanItemModeLabels.Length - 1);
                        SaveXagmanSharedItemsState();
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
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
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
        var importedItems = package.Items
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = searchOnly ? XagmanItemMode.Give : group.First().Mode,
                Quantity = searchOnly ? 0 : Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
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
                    IsHq = false,
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
                IsHq = false,
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
                xagmanRunning && xagmanActiveRole == XagmanRole.Tony ? xagmanActiveCharacter : string.Empty,
                runner);
        }

        if (hasTonyPlan && hasOwnerPlan)
            ImGui.Spacing();

        if (hasOwnerPlan)
        {
            DrawXagmanProcessingList(
                "Franchise Owner Order",
                xagmanOwnerRunPlan,
                GetXagmanLocalOwnerCompletedCharacters(),
                xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner ? xagmanActiveCharacter : string.Empty,
                runner);
        }
    }

    private void DrawXagmanProcessingList(string label, IReadOnlyList<string> runPlan, int completed, string activeCharacter, TaskRunner runner)
    {
        if (runPlan.Count == 0)
            return;

        var safeCompleted = Math.Max(0, Math.Min(completed, runPlan.Count));
        ImGui.TextDisabled($"{label} ({safeCompleted}/{runPlan.Count})");
        for (var i = 0; i < runPlan.Count; i++)
        {
            var character = runPlan[i];
            var failed = runner.FailedCharacters.Any(entry => entry.Equals(character, StringComparison.OrdinalIgnoreCase));
            var isActive = !string.IsNullOrWhiteSpace(activeCharacter)
                && character.Equals(activeCharacter, StringComparison.OrdinalIgnoreCase);

            if (failed)
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  [x] {i + 1}. {character}");
            else if (isActive)
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"  [>] {i + 1}. {character}");
            else if (i < safeCompleted)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"  [v] {i + 1}. {character}");
            else
                ImGui.TextDisabled($"  [ ] {i + 1}. {character}");
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

        if (ImGui.BeginTable("XagmanPeersTable", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, ScaledVector(0f, 100f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
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

    private List<XagmanPeerPresence> GetXagmanCommandTargetPeers()
    {
        if (plugin.XagmanPeers == null || plugin.XagmanPeers.IsDisposed)
            return new List<XagmanPeerPresence>();

        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled)
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

    private async System.Threading.Tasks.Task<List<XagmanPeerPresence>> WaitForXagmanCommandTargetPeersAsync(double timeoutSeconds)
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadlineUtc)
        {
            var peers = GetXagmanCommandTargetPeers();
            if (peers.Count > 0)
                return peers;
            await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
        }

        return GetXagmanCommandTargetPeers();
    }

    private void StartAllXagmanPeers()
    {
        _ = StartAllXagmanPeersAsync();
    }

    private async System.Threading.Tasks.Task StartAllXagmanPeersAsync()
    {
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

            await AddXagmanPeerLogAsync($"Xagman: Sending start command to {peers.Count} connected peers...").ConfigureAwait(false);

            if (await plugin.XagmanPeers.SendStartTaskToAllPeersAsync().ConfigureAwait(false))
                await AddXagmanPeerLogAsync("Xagman: Start command sent successfully").ConfigureAwait(false);
            else
                await AddXagmanPeerLogAsync($"Xagman: Failed to send start command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send start command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] StartAllXagmanPeers failed");
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
                if (!xagmanRunning)
                    return;

                StopXagmanTask();
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

    private void CompleteAllXagmanPeers()
    {
        _ = CompleteAllXagmanPeersAsync();
    }

    private async System.Threading.Tasks.Task CompleteAllXagmanPeersAsync()
    {
        try
        {
            if (!await EnsureXagmanPeerCommandChannelAsync("completion").ConfigureAwait(false))
                return;

            var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
            if (peers.Count == 0)
            {
                await AddXagmanPeerLogAsync("Xagman: No connected peers to send completion command to").ConfigureAwait(false);
                return;
            }

            await AddXagmanPeerLogAsync($"Xagman: Sending completion command to {peers.Count} connected peers...").ConfigureAwait(false);
            if (await plugin.XagmanPeers.SendCompleteTaskToAllPeersAsync().ConfigureAwait(false))
                await AddXagmanPeerLogAsync("Xagman: Completion command sent successfully").ConfigureAwait(false);
            else
                await AddXagmanPeerLogAsync($"Xagman: Failed to send completion command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send completion command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] CompleteAllXagmanPeers failed");
        }
    }

    private void InitializeXagmanPeerEventHandlers()
    {
        // Subscribe to peer service task control events
        plugin.XagmanPeers.OnTaskStartRequested -= HandlePeerTaskStartRequest;
        plugin.XagmanPeers.OnTaskStopRequested -= HandlePeerTaskStopRequest;
        plugin.XagmanPeers.OnTaskRecallRequested -= HandlePeerTaskRecallRequest;
        plugin.XagmanPeers.OnTaskCompleteRequested -= HandlePeerTaskCompleteRequest;
        plugin.XagmanPeers.OnTaskStartRequested += HandlePeerTaskStartRequest;
        plugin.XagmanPeers.OnTaskStopRequested += HandlePeerTaskStopRequest;
        plugin.XagmanPeers.OnTaskRecallRequested += HandlePeerTaskRecallRequest;
        plugin.XagmanPeers.OnTaskCompleteRequested += HandlePeerTaskCompleteRequest;
    }

    private void HandlePeerTaskStartRequest()
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
                                if (StartXagmanFranchiseTask(true, true))
                                    plugin.TaskRunner.AddLog("Xagman: Resumed standby owner via peer command.");
                                else
                                    plugin.TaskRunner.AddLog("Xagman: Could not resume standby owner via peer command.");
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
                            if (StartXagmanFranchiseTask(true))
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
                    // Stop any running Xagman task
                    if (xagmanRunning)
                    {
                        StopXagmanTask();
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

    private void HandlePeerTaskCompleteRequest()
    {
        try
        {
            Plugin.Framework.Run(() =>
            {
                try
                {
                    if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
                        return;

                    StartXagmanFranchiseCompletionTask("Xagman: Tony supply is depleted across all selected Tonys; starting owner completion cleanup.");
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
        var label = string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld)
            ? "Select World##xagmanWorldButton"
            : $"{cfg.XagmanTargetWorld}##xagmanWorldButton";
        if (ImGui.Button(label))
            ImGui.OpenPopup("XagmanWorldPopup");
        if (!ImGui.BeginPopup("XagmanWorldPopup"))
            return;
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanWorldFilter", "Type a world name...", ref xagmanWorldFilter, 128);
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
        if (string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld))
        {
            ImGui.BeginDisabled();
            ImGui.Button("Select Location##xagmanAetheryteButton");
            ImGui.EndDisabled();
            return;
        }
        ImGui.SameLine();
        var label = string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)
            ? "Select Location##xagmanAetheryteButton"
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
    private bool IsXagmanFranchiseCharacterVisible(Configuration cfg, string charName, int index)
    {
        var world = GetWorldFromKey(charName);
        var regionDc = WorldData.GetRegionDcLabel(world);
        if (!MatchesRegionFilter(world, cfg.XagmanRegionFilter))
            return false;
        if (xagmanFranchiseShowOnlySelected && !xagmanFranchiseSelectedIndices.Contains(index))
            return false;
        return string.IsNullOrWhiteSpace(xagmanFranchiseSearchFilter)
            || charName.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase)
            || world.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase)
            || regionDc.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase);
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
    }
    private void SelectVisibleXagmanFranchiseCharacters()
    {
        var cfg = plugin.Configuration;
        foreach (var index in GetVisibleXagmanFranchiseCharacterIndices(cfg))
            xagmanFranchiseSelectedIndices.Add(index);
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
    private void SelectXagmanTonyCharactersWithMatchingItems()
    {
        QueueXagmanMatchingSelection(XagmanMatchSelectionTarget.Tony);
    }
    private void SelectXagmanFranchiseCharactersWithMatchingItems()
    {
        ClearXagmanMatchingSelectionCaches();
        var cfg = plugin.Configuration;
        var ignoreGil = cfg.XagmanIgnoreGilInMatchingSelection;
        var visibleIndices = GetVisibleXagmanFranchiseCharacterIndices(cfg);
        if (visibleIndices.Count == 0)
        {
            xagmanFranchiseSelectedIndices.Clear();
            arImportStatus = "Xagman: no visible Franchise Owner characters to match.";
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

        var selectAllVisible = items.Any(item => item.Mode == XagmanItemMode.Take && (!IsXagmanGilItem(item.ItemId) || item.Quantity != 0));
        xagmanFranchiseSelectedIndices.Clear();
        var selectedCount = 0;
        foreach (var index in visibleIndices)
        {
            var characterNameWorld = cfg.XagmanFranchiseCharacters[index];
            if (!selectAllVisible && !DoesXagmanFranchiseCharacterNeedItemChanges(characterNameWorld, items, ignoreGil))
                continue;

            xagmanFranchiseSelectedIndices.Add(index);
            selectedCount++;
        }

        arImportStatus = selectAllVisible
            ? $"Xagman: selected {selectedCount} visible Franchise Owner character{(selectedCount == 1 ? string.Empty : "s")} because Take always requires processing."
            : $"Xagman: selected {selectedCount} visible Franchise Owner character{(selectedCount == 1 ? string.Empty : "s")} that actually need item changes.";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private bool DoesXagmanFranchiseCharacterNeedItemChanges(string characterNameWorld, IReadOnlyList<XagmanItemEntry> items, bool ignoreGil)
    {
        foreach (var item in items)
        {
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
                .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
                .Select(item => BuildXagmanMatchItemKey(item.ItemId, item.IsHq))
                .Distinct()
                .OrderBy(itemKey => itemKey, StringComparer.Ordinal));
    }
    private void QueueXagmanMatchingSelection(XagmanMatchSelectionTarget target)
    {
        ClearXagmanMatchingSelectionCaches();
        var cfg = plugin.Configuration;
        var ignoreGil = cfg.XagmanIgnoreGilInMatchingSelection;
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
        var sourceItems = target == XagmanMatchSelectionTarget.Tony
            ? cfg.XagmanTonyItems
            : cfg.XagmanItems;
        var matchingItems = sourceItems
            .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => group.First())
            .ToList();
        var visibleCharacterKeys = GetVisibleXagmanCharacterKeys(cfg, target);
        if (visibleCharacterKeys.Count == 0)
        {
            if (target == XagmanMatchSelectionTarget.Tony)
                xagmanTonySelectedIndices.Clear();
            else
                xagmanFranchiseSelectedIndices.Clear();
            xagmanPendingMatchSelection = null;
            arImportStatus = $"Xagman: no visible {selectionLabel} characters to match.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            return;
        }
        var itemsKey = BuildXagmanMatchingItemsKey(matchingItems, ignoreGil);
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
        var matches = GetXagmanMatchingCharacterKeys(matchingItems, ignoreGil);
        ApplyXagmanMatchingSelection(target, visibleCharacterKeys, matches);
    }
    private void ApplyXagmanMatchingSelection(XagmanMatchSelectionTarget target, IReadOnlyCollection<string> visibleCharacterKeys, HashSet<string> matches)
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
        arImportStatus = $"Xagman: selected {selectedCount} visible {selectionLabel} character{(selectedCount == 1 ? string.Empty : "s")} with matching items.";
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
        ApplyXagmanMatchingSelection(request.Target, request.VisibleCharacterKeys, request.MatchKeys);
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

        if (plugin.IpcClient.TryGetMatchingCharactersForItems(string.Join(",", itemKeys), out var rawMatches))
        {
            return rawMatches
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil) && !string.IsNullOrWhiteSpace(item.ItemName)))
        {
            foreach (var result in SearchXagmanCharacterMatches(item.ItemName))
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
        if (!plugin.IpcClient.IsXaDatabaseAvailable() || string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return new List<XagmanItemSearchEntry>();
        return SearchXagmanCharacterMatches(query)
            .GroupBy(match => new { match.ItemId, match.ItemName, match.IsHq })
            .Select(group => new XagmanItemSearchEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.Key.ItemName,
                IsHq = group.Key.IsHq,
            })
            .OrderBy(entry => entry.ItemId)
            .Take(100)
            .ToList();
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
            if (TryParseXagmanSearchMatch(line, out var match))
                results.Add(match);
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
            ItemId = itemId,
            ItemName = parts[3],
            Quantity = quantity,
            IsHq = isHq,
        };
        return true;
    }
    private void AddXagmanItem(List<XagmanItemEntry> items, uint itemId, string itemName, bool isHq)
    {
        if (items.Any(entry => entry.ItemId == itemId && entry.IsHq == isHq))
            return;
        items.Add(new XagmanItemEntry
        {
            ItemId = itemId,
            ItemName = itemName,
            IsHq = isHq,
            Mode = XagmanItemMode.Give,
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
    }

    private static List<XagmanItemEntry> CloneXagmanItems(IEnumerable<XagmanItemEntry> items, bool searchOnly = false)
    {
        return items
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = searchOnly ? XagmanItemMode.Give : group.First().Mode,
                Quantity = searchOnly ? 0 : Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
    }

    private void SetAllXagmanItemModes(IEnumerable<XagmanItemEntry> items, XagmanItemMode mode)
    {
        var changed = false;
        foreach (var item in items)
        {
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
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) || string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte))
            return;
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearLog();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
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
        xagmanPreferredTonyCharacter = selected[0].CharacterNameWorld;
        StartXagmanTonyStartup(selected[0], true);
    }

    private bool StartXagmanFranchiseTask(bool startSignalReceived = false, bool resumeFromStandby = false)
    {
        var cfg = plugin.Configuration;
        var savedOwnerRunListCount = xagmanOwnerRunList.Count;
        var storedOwnerIndex = Math.Max(0, xagmanOwnerCurrentCharacterIndex);
        var selected = resumeFromStandby && savedOwnerRunListCount > 0
            ? xagmanOwnerRunList.ToList()
            : GetSelectedXagmanFranchiseCharacters();
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
        HaltAutoCollectionForPriorityTask("Xagman");
        if (!resumeFromStandby)
            plugin.TaskRunner.ClearLog();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        SetXagmanRunning(true);
        xagmanActiveRole = XagmanRole.FranchiseOwner;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "Standing by for Tony meetup acknowledgement.";
        if (!resumeFromStandby || xagmanOwnerRunPlan.Count == 0)
            xagmanOwnerRunPlan = selected.ToList();
        if (!resumeFromStandby)
            xagmanTonyRunPlan = Array.Empty<string>();
        xagmanOwnerRunList = selected.ToList();
        xagmanOwnerTotalCharacters = selected.Count;
        xagmanOwnerCompletedCharacters = Math.Max(0, Math.Min(startIndex, xagmanOwnerTotalCharacters));
        xagmanOwnerCurrentCharacterIndex = startIndex;
        if (!startSignalReceived)
            xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanOwnerStandbyPending = false;
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
        if (!resumeFromStandby)
            SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        TryResolveXagmanMeetDestinationForOwner();
        if (ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(xagmanActiveCharacter))
            TryRequireXagmanReceiverAutoAccept($"owner {xagmanActiveCharacter} pending Tony supply");
        PublishXagmanPresence();
        var steps = BuildXagmanFranchiseSteps(selected, startIndex);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: OnXagmanFranchiseTaskFinished, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = GetXagmanLocalOwnerCompletedCharacters();
        return true;
    }

    private void SetXagmanRunning(bool value)
    {
        if (xagmanRunning == value)
        {
            plugin.TargetCommandFix.SetRequiredByXagman(value);
            return;
        }

        xagmanRunning = value;
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
        xagmanActiveCharacter = entry.CharacterNameWorld;
        xagmanPreferredTonyCharacter = entry.CharacterNameWorld;
        xagmanTonyMode = entry.Mode;
        xagmanTonyObservedOwnerWork = false;
        ResetXagmanTonySellLocation();
        ResetXagmanTonyMeetRetryState();
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
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
            SetXagmanRunning(false);
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
            IsComplete = () => !string.IsNullOrWhiteSpace(plugin.Configuration.XagmanTargetWorld)
                && !string.IsNullOrWhiteSpace(plugin.Configuration.XagmanTargetAetheryte),
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
                SetXagmanActiveMeetDestination(plugin.Configuration.XagmanTargetWorld, plugin.Configuration.XagmanTargetAetheryte);
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
            });
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
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();
        ResetXagmanTonyMeetRetryState();
        TrySetXagmanDropboxAutoAccept(false);
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        xagmanActiveRole = plugin.Configuration.XagmanRole;
        xagmanStatus = XagmanStatus.Idle;
        xagmanStatusText = "Idle";
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
        xagmanOwnerRequestedItems.Clear();
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
        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
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
                TrySetXagmanDropboxAutoAccept(false);
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
            });
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
                TrySetXagmanDropboxAutoAccept(false);
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
                });
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
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: () => FinalizeXagmanLocalShutdown("Franchise Owner peer completion"), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private List<TaskStep> BuildXagmanFranchiseSteps(List<string> characters, int startIndex)
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
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
                if (relogFailed || standbyRequested || xagmanOwnerPauseForTonyRotationRequested)
                    return true;
                if (YieldOwnerTradeLockIfNeeded())
                    return true;
                return TryEnterStandby();
            }

            bool ShouldSkipOwnerCollectionTradeExecution()
            {
                return ShouldSkipTradeFlow() || ownerCollectionQueuedEntries <= 0;
            }

            bool ShouldSkipRequestedTradeFlow()
            {
                return ShouldSkipTradeFlow() || xagmanOwnerRequestedItems.Count == 0;
            }

            bool ShouldArmOwnerAutoAcceptForPendingTonySupply() => ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(charName);

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
                        return BeginStandbyForTonyRotation(GetActiveTradeLockLostMessage());
                    return false;
                }

                if (HasXagmanOwnerCollectionTradeCompleted(cfg.XagmanItems, charName))
                    return true;
                if (!HasXagmanActiveTonyTradeLock(charName))
                    return BeginStandbyForTonyRotation(GetActiveTradeLockLostMessage());
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

                if (!xagmanObservedDropboxBusy && !HasXagmanActiveTonyTradeLock(charName))
                {
                    var remainingRequestsWithoutTradeLock = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                    if (remainingRequestsWithoutTradeLock.Count == 0)
                    {
                        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                        return true;
                    }
                    return BeginStandbyForTonyRotation(GetActiveTradeLockLostMessage());
                }

                var remainingRequests = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                if (remainingRequests.Count == 0)
                {
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    return true;
                }

                if (!HasXagmanActiveTonyTradeLock(charName))
                {
                    SetXagmanOwnerRequestedItems(remainingRequests, false);
                    return BeginStandbyForTonyRotation(GetActiveTradeLockLostMessage());
                }

                if (HasXagmanRequestedTradeProgress(xagmanOwnerRequestedItems, charName))
                {
                    SetXagmanOwnerRequestedItems(remainingRequests, false);
                    return false;
                }

                return false;
            }

            void EvaluateOwnerCollectionRetry(int collectionPassNumber)
            {
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
                ownerCollectionRetryRequested = HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName);
                var remainingRequestedItems = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                if (!ownerCollectionRetryRequested && remainingRequestedItems.Count == 0)
                {
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    ownerSendoffVerified = true;
                    runner.AddLog($"Xagman: owner {charName} completion verification {verificationPassNumber}/2 passed; no additional give or Tony supply work remains before sendoff.");
                    return true;
                }

                SetXagmanOwnerRequestedItems(remainingRequestedItems, false);
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
                Name = $"Xagman Owner Begin: {charName}",
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
                        SetXagmanOwnerRequestedItems(refreshedRequestedItems, false);
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
                    if (relogFailed)
                        return;
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = $"Traveling owner {charName} to {GetXagmanActiveMeetDestinationLabel()}.";
                },
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
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Owner {charName} failed to reach the meet spot.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Wait For Tony Available: {charName}",
                ShouldSkip = () => relogFailed,
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
                    if (relogFailed)
                        return true;
                    TryResolveXagmanMeetDestinationForOwner();
                    TryBindXagmanFranchiseTonyForMeetup();
                    return !string.IsNullOrWhiteSpace(GetXagmanOwnerQueueTonyCharacter());
                },
                TimeoutSec = 600f,
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
                ShouldSkip = () => relogFailed,
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
                    if (relogFailed)
                        return true;
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
                TimeoutSec = 600f,
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
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    EnterOwnerTonyQueue(true);
                },
                IsComplete = () => relogFailed || IsXagmanOwnerCalled(charName),
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
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
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
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
                        return;
                    OpenXagmanDropboxTradeTab();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Open Item Tab Wait: {charName}", 1.0f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Clear Queue: {charName}",
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
                        return;
                    ClearXagmanDropbox();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Queue Items: {charName}",
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
                        return;
                    ownerCollectionQueuedEntries = QueueXagmanOwnerCollectionItems(cfg.XagmanItems);
                    if (ownerCollectionQueuedEntries <= 0)
                        runner.AddLog($"Xagman: owner {charName} had nothing queued for the owner collection pass; skipping Dropbox trade start and moving to Tony request evaluation.");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Queue Wait: {charName}", 0.5f, ShouldSkipTradeFlow));
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
                ShouldSkip = () => relogFailed || standbyRequested || ownerCollectionRetryRequested,
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    var requestedItems = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName);
                    SetXagmanOwnerRequestedItems(requestedItems);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
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
                    TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => relogFailed || ShouldSkipRequestedTradeFlow() || IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance),
                TimeoutSec = 5f,
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
            AppendXagmanDropboxAutoAcceptStep(steps, $"Xagman Requested Trade {charName}", false, () => standbyRequested);
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
                    () => standbyRequested);
            }
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Complete: {charName}",
                ShouldSkip = () => relogFailed || standbyRequested,
                OnEnter = () =>
                {
                    xagmanOwnerCompletedCharacters = charIndex;
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
                    PublishXagmanPresence();
                },
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
                SetXagmanRunning(false);
                xagmanOwnerCurrentCharacterIndex = characters.Count;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = runner.FailedCharacters.Count == 0
                    ? "Franchise Owner run completed."
                    : $"Franchise Owner run finished with {runner.FailedCharacters.Count} failures.";
                runner.SuppressLogoutCancel = !MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete);
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
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
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        return steps;
    }

    private void AddXagmanRelogSteps(List<TaskStep> steps, string charName, TaskRunner runner, SysAction onEnter, SysAction onReady, SysAction onTimeout)
    {
        var relogFailed = false;
        var skipRelog = false;
        var recentlyReturnedToFc = false;
        var returnToFcBeforeRelog = false;
        var loggedInCharacter = string.Empty;
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog Setup: {charName}",
            OnEnter = () =>
            {
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
            () => relogFailed || skipRelog || !returnToFcBeforeRelog);
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog: {charName}",
            ShouldSkip = () => relogFailed || skipRelog,
            OnEnter = () =>
            {
                runner.AddLog(returnToFcBeforeRelog
                    ? $"Xagman: relogging to {charName} after returning {loggedInCharacter} to FC."
                    : $"Xagman: relogging to {charName}.");
                ChatHelper.SendMessage($"/ays relog {charName}");
            },
            IsComplete = () => MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                && CharacterSafetyHelper.IsCharacterSafeWaitReady(),
            TimeoutSec = 600f,
            MaxRetries = 2,
            OnTimeout = () =>
            {
                relogFailed = true;
                onTimeout();
            },
        });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Relog Ready: {charName}",
            ShouldSkip = () => relogFailed,
            OnEnter = onReady,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private void AddXagmanTeleportSteps(List<TaskStep> steps, string label, Func<string> commandProvider, TaskRunner runner, Func<bool>? alreadyThere, bool allowNoBusy, SysAction onEnter, SysAction onReady, SysAction onTimeout, Func<bool>? externalSkip = null)
    {
        var skipTeleport = false;
        var sawBusy = false;
        var teleportFailed = false;

        bool ShouldExternalSkip()
        {
            return externalSkip?.Invoke() ?? false;
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
                    plugin.IpcClient.LifestreamExecuteCommand(commandProvider());
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
                    return true;
                }
                if (alreadyThere?.Invoke() ?? false)
                {
                    skipTeleport = true;
                    return true;
                }
                return false;
            },
            TimeoutSec = allowNoBusy ? 15f : 45f,
            OnTimeout = () =>
            {
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
                    return true;
                var busy = plugin.IpcClient.LifestreamIsBusy();
                if (busy)
                {
                    sawBusy = true;
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
                return alreadyThere == null;
            },
            TimeoutSec = 600f,
            OnTimeout = () =>
            {
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
            OnEnter = onReady,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private void UpdateXagmanFrameworkTick()
    {
        var totalStopwatch = Stopwatch.StartNew();
        MeasureFrameworkUpdateStep("Xagman.ProcessPendingMatchSelection", ProcessXagmanPendingMatchSelection);
        if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony && !plugin.TaskRunner.IsRunning)
            MeasureFrameworkUpdateStep("Xagman.UpdateTonyRuntime", UpdateXagmanTonyRuntime);
        var publishInterval = xagmanRunning ? 1.0 : 5.0;
        if ((DateTime.UtcNow - xagmanLastPresencePublishUtc).TotalSeconds >= publishInterval)
            MeasureFrameworkUpdateStep("Xagman.PublishPresence", PublishXagmanPresence);
        MeasureFrameworkUpdateStep("Xagman.UpdatePriorityTaskExternalStatus", UpdatePriorityTaskExternalStatus);
        totalStopwatch.Stop();
        LogFrameworkUpdateStepDuration("Xagman.UpdateXagmanFrameworkTick", totalStopwatch.Elapsed.TotalMilliseconds);
    }

    private bool TryReassertXagmanTonyMeetup()
    {
        const int maxMeetRetries = 3;
        const double retryCooldownSeconds = 3.0;

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
        if (IsXagmanAtMeetDestination(meetWorld, meetAetheryte))
        {
            ResetXagmanTonyMeetRetryState();
            return false;
        }

        xagmanStatus = XagmanStatus.Traveling;
        if (plugin.IpcClient.LifestreamIsBusy())
        {
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is still traveling to {GetXagmanActiveMeetDestinationLabel()}.";
            return true;
        }

        if ((DateTime.UtcNow - xagmanTonyLastMeetRetryUtc).TotalSeconds < retryCooldownSeconds)
        {
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is retrying meetup travel to {GetXagmanActiveMeetDestinationLabel()}.";
            return true;
        }

        if (xagmanTonyMeetRetryCount >= maxMeetRetries)
        {
            var failedRetryCount = xagmanTonyMeetRetryCount;
            ResetXagmanTonyMeetRetryState();
            plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} failed meetup recheck {failedRetryCount} times; rotating Tony.");
            RotateXagmanTony();
            return true;
        }

        var destinationCommand = GetXagmanActiveMeetDestinationCommand();
        if (string.IsNullOrWhiteSpace(destinationCommand))
            return false;

        xagmanTonyLastMeetRetryUtc = DateTime.UtcNow;
        xagmanTonyMeetRetryCount++;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} is retrying meetup travel to {GetXagmanActiveMeetDestinationLabel()} ({xagmanTonyMeetRetryCount}/{maxMeetRetries}).";
        plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} is not at the meet spot; retrying Lifestream command ({xagmanTonyMeetRetryCount}/{maxMeetRetries}).");
        plugin.IpcClient.LifestreamExecuteCommand(destinationCommand);
        return true;
    }

    private void UpdateXagmanTonyRuntime()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony)
            return;
        if (xagmanStatus == XagmanStatus.Error)
            return;
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
                TrySetXagmanDropboxAutoAccept(false);
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
            var activePartnerPeer = plugin.XagmanPeers.Peers
                .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.FranchiseOwner)
                .FirstOrDefault(peer => peer.ActiveCharacter.Equals(xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase));
            var activePartnerRequestedItems = activePartnerPeer?.RequestedItems == null
                ? new List<XagmanTradeRequestEntry>()
                : CloneXagmanTradeRequests(activePartnerPeer.RequestedItems);
            if (!xagmanObservedDropboxBusy && activePartnerRequestedItems.Count > 0)
            {
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"Tony {xagmanActiveCharacter} is resupplying {xagmanActiveTradePartner}.";
                if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
                    return;
                if (activePartnerPeer != null)
                    xagmanActiveTradePartnerInstanceId = activePartnerPeer.InstanceId;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                plugin.TaskRunner.AddLog($"Xagman: Tony resumed active owner {xagmanActiveTradePartner} for {activePartnerRequestedItems.Count} requested supply entr{(activePartnerRequestedItems.Count == 1 ? "y" : "ies")}.");
                StartXagmanTonyTrade(activePartnerRequestedItems, activePartnerPeer);
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
                : inFlightOwner.RequestedItems
                    .Where(entry => entry.ItemId > 0)
                    .Select(entry => new XagmanTradeRequestEntry
                    {
                        ItemId = entry.ItemId,
                        ItemName = entry.ItemName,
                        IsHq = entry.IsHq,
                        Mode = entry.Mode,
                        Quantity = entry.Quantity,
                        TargetQuantity = entry.TargetQuantity,
                        CurrentQuantity = entry.CurrentQuantity,
                    })
                    .ToList();
            if (inFlightRequestedItems.Count > 0)
            {
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"Tony {xagmanActiveCharacter} is resupplying {inFlightOwner.ActiveCharacter}.";
                if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
                    return;
                xagmanActiveTradePartner = inFlightOwner.ActiveCharacter;
                xagmanActiveTradePartnerInstanceId = inFlightOwner.InstanceId;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                plugin.TaskRunner.AddLog($"Xagman: Tony resumed {inFlightOwner.ActiveCharacter} for {inFlightRequestedItems.Count} requested supply entr{(inFlightRequestedItems.Count == 1 ? "y" : "ies")}.");
                StartXagmanTonyTrade(inFlightRequestedItems, inFlightOwner);
                return;
            }
            if (string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                || !xagmanActiveTradePartner.Equals(inFlightOwner.ActiveCharacter, StringComparison.OrdinalIgnoreCase))
            {
                xagmanActiveTradePartner = inFlightOwner.ActiveCharacter;
                xagmanActiveTradePartnerInstanceId = inFlightOwner.InstanceId;
                if (!TryRequireXagmanReceiverAutoAccept($"Tony receiving from {inFlightOwner.ActiveCharacter}"))
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to enable Dropbox auto-accept while receiving from {inFlightOwner.ActiveCharacter}.";
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    return;
                }
            }
            xagmanStatus = inFlightOwner.Status == XagmanStatus.Trading ? XagmanStatus.Trading : XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting for {inFlightOwner.ActiveCharacter} to finish the active trade handoff.";
            return;
        }
        if (queue.Count == 0)
        {
            if (TryStartXagmanTonyCompletion())
                return;
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting at the meet spot.";
            return;
        }
        var next = queue[0];
        var requestedItems = next.RequestedItems == null
            ? new List<XagmanTradeRequestEntry>()
            : next.RequestedItems
                .Where(entry => entry.ItemId > 0)
                .Select(entry => new XagmanTradeRequestEntry
                {
                    ItemId = entry.ItemId,
                    ItemName = entry.ItemName,
                    IsHq = entry.IsHq,
                    Mode = entry.Mode,
                    Quantity = entry.Quantity,
                    TargetQuantity = entry.TargetQuantity,
                    CurrentQuantity = entry.CurrentQuantity,
                })
                .ToList();
        var hasRequestedItems = requestedItems.Count > 0;
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} sees {queue.Count} owner(s) in queue.";
        if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
            return;
        xagmanActiveTradePartner = next.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = next.InstanceId;
        if (!hasRequestedItems)
        {
            if (!TryRequireXagmanReceiverAutoAccept($"Tony receiving from {next.ActiveCharacter}"))
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Failed to enable Dropbox auto-accept while receiving from {next.ActiveCharacter}.";
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                return;
            }
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
        var ownerPeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.FranchiseOwner)
            .FirstOrDefault(peer => peer.ActiveCharacter.Equals(activePartner, StringComparison.OrdinalIgnoreCase));
        if (ownerPeer != null)
        {
            var ownerStillInTradeFlow = ownerPeer.Status is XagmanStatus.Called or XagmanStatus.Trading
                || ownerPeer.QueueRequestedAtUtc > DateTime.MinValue;
            if (ownerStillInTradeFlow)
                return false;
        }
        TrySetXagmanDropboxAutoAccept(false);
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
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
            StartXagmanTonyCompletionTask(completionRequester.ActiveCharacter, autoDetectedNoRemainingOwners: false, completedWithWarnings: false);
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
            StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: true, completedWithWarnings: false);
            return true;
        }
        var remainingFranchiseOwners = GetXagmanRemainingFranchiseOwnerCountForTony(xagmanActiveCharacter, freshOnly: true);
        if (remainingFranchiseOwners > 0)
            return false;
        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: true, completedWithWarnings: false);
        return true;
    }

    private void StartXagmanTonyCompletionTask(string requestedBy, bool autoDetectedNoRemainingOwners = false, bool completedWithWarnings = false, bool broadcastPeerCompletion = false)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var tonyCharacter = xagmanActiveCharacter;
        var steps = new List<TaskStep>();
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Summary: {tonyCharacter}",
            OnEnter = () =>
            {
                TrySetXagmanDropboxAutoAccept(false);
                ClearXagmanDropbox();
                if (broadcastPeerCompletion)
                    CompleteAllXagmanPeers();
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
                runner.SuppressLogoutCancel = !MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete);
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
                });
        }
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: () => FinalizeXagmanLocalShutdown("Tony completion", disconnectBeforeStop: true), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void HandleXagmanTonySupplyDepletion(IReadOnlyList<XagmanTradeRequestEntry> requestedItems, string partnerName)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;

        var requestedEntries = requestedItems.Count(entry => entry.ItemId > 0);
        var requestedUnits = requestedItems
            .Where(entry => entry.ItemId > 0)
            .Sum(entry => Math.Max(0, entry.Quantity));
        var activeTony = xagmanActiveCharacter;
        var hasAlternateTony = xagmanTonyRunList.Any(key => !key.Equals(activeTony, StringComparison.OrdinalIgnoreCase));
        TrySetXagmanDropboxAutoAccept(false);
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanObservedDropboxBusy = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;

        if (hasAlternateTony)
        {
            xagmanStatus = XagmanStatus.ReturningHome;
            xagmanStatusText = $"Tony {activeTony} depleted requested supply for {partnerName}; rotating to the next Tony.";
            plugin.TaskRunner.AddLog($"Xagman: Tony {activeTony} cannot currently supply {requestedEntries} requested entr{(requestedEntries == 1 ? "y" : "ies")} totaling {requestedUnits} units to {partnerName}; rotating to the next Tony.");
            RotateXagmanTony();
            return;
        }

        plugin.TaskRunner.AddLog($"Xagman: Tony {activeTony} cannot currently supply {requestedEntries} requested entr{(requestedEntries == 1 ? "y" : "ies")} totaling {requestedUnits} units to {partnerName}, and no alternate Tony remains; finalizing with warning summary and peer completion cleanup.");
        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: false, completedWithWarnings: true, broadcastPeerCompletion: true);
    }

    private void StartXagmanTonyTrade(IReadOnlyList<XagmanTradeRequestEntry>? requestedItems = null, XagmanPeerPresence? ownerPeer = null)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning || string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return;
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
            TrySetXagmanDropboxAutoAccept(false);
            ClearXagmanFocusTarget();
            xagmanObservedDropboxBusy = false;
            xagmanActiveTradePartner = string.Empty;
            xagmanActiveTradePartnerInstanceId = string.Empty;
            xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        }
        var usingSupplyRequests = requestedItems?.Any(entry => entry.ItemId > 0) == true;
        var supplyRequests = usingSupplyRequests
            ? BuildXagmanTonySupplyRequests(requestedItems!)
            : new List<XagmanTradeRequestEntry>();
        var items = plugin.Configuration.XagmanItems.ToList();
        if (!usingSupplyRequests && items.Count == 0)
        {
            plugin.TaskRunner.AddLog("Xagman: shared item list is empty, skipping trade.");
            return;
        }
        if (usingSupplyRequests && supplyRequests.Count == 0)
        {
            HandleXagmanTonySupplyDepletion(requestedItems!, partnerName);
            return;
        }
        if (usingSupplyRequests)
            plugin.TaskRunner.AddLog($"Xagman: Tony will supply {supplyRequests.Count} requested item entr{(supplyRequests.Count == 1 ? "y" : "ies")} to {partnerName}.");
        bool PollTonyTradeWait()
        {
            var ownerStandbyRotationReady = TryObserveXagmanOwnerStandbyRotationRequest();
            if (ownerStandbyRotationReady.HasValue)
                return ownerStandbyRotationReady.Value;

            var busy = plugin.IpcClient.DropboxIsBusy();
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
                    TrySetXagmanDropboxAutoAccept(false);
                    ClearXagmanDropbox();
                    xagmanObservedDropboxBusy = false;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                    return true;
                }

                return false;
            }

            var liveOwnerPeer = GetXagmanActiveTradeOwnerPeer() ?? ownerPeer;
            var remainingRequests = liveOwnerPeer?.RequestedItems?.Count(entry => entry.ItemId > 0) ?? requestedItems?.Count(entry => entry.ItemId > 0) ?? 0;
            if (liveOwnerPeer != null && remainingRequests == 0)
            {
                xagmanObservedDropboxBusy = false;
                return true;
            }

            if (xagmanObservedDropboxBusy)
            {
                runner.AddLog($"Xagman: Dropbox trading queue ended for {partnerName} before owner confirmed requested supply; releasing the trade lock for owner reconciliation.");
                TrySetXagmanDropboxAutoAccept(false);
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
                OnEnter = ClearXagmanDropbox,
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Queue Items {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
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
                OnEnter = () => TrySetXagmanDropboxAutoAccept(false),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Start {partnerName}",
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
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
                OnEnter = () => TrySetXagmanDropboxAutoAccept(false),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
        };
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true);
        ApplyXagmanTonyTradeProgressToTaskRunner(partnerName, ownerPeer);
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

        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => DoesXagmanPeerMatchTradePartner(peer, xagmanActiveTradePartner))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)
                && peer.InstanceId.Equals(xagmanActiveTradePartnerInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter)
                && peer.ActiveCharacter.Equals(xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
    }
    private void RotateXagmanTony()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;
        ResetXagmanTonyMeetRetryState();
        MarkXagmanTonyConsumed(xagmanActiveCharacter);
        if (xagmanTonyRunList.Count == 0)
            return;
        xagmanCurrentTonyIndex = 0;
        var nextKey = xagmanTonyRunList[xagmanCurrentTonyIndex];
        var nextEntry = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(entry => entry.CharacterNameWorld.Equals(nextKey, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = nextKey, Mode = xagmanTonyMode };
        plugin.TaskRunner.AddLog($"Xagman: rotating Tony to {nextEntry.CharacterNameWorld}.");
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        ResetXagmanTonySellLocation();
        TrySetXagmanDropboxAutoAccept(false);
        StartXagmanTonyStartup(nextEntry, true);
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
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
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
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status == XagmanStatus.AtMeetSpot)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
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
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status is XagmanStatus.AtMeetSpot or XagmanStatus.Called or XagmanStatus.Trading)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
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
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status is XagmanStatus.AtMeetSpot or XagmanStatus.Called or XagmanStatus.Trading)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
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

    private bool TryGetXagmanMeetDestinationForOwner(out string meetWorld, out string meetAetheryte)
    {
        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        var tonyPeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.MeetWorld))
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

    private bool TryResolveXagmanMeetDestinationForOwner()
    {
        if (!TryGetXagmanMeetDestinationForOwner(out var meetWorld, out var meetAetheryte))
            return false;

        var destinationChanged = !meetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase)
            || !meetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase);
        SetXagmanActiveMeetDestination(meetWorld, meetAetheryte);
        if (destinationChanged)
            PublishXagmanPresence();
        return !string.IsNullOrWhiteSpace(xagmanActiveMeetWorld);
    }

    private bool IsXagmanFranchiseStartupReady()
    {
        if (!xagmanOwnerStartRequested)
            return false;
        if (!TryResolveXagmanMeetDestinationForOwner())
            return false;
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
        var requestedItems = xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner
            ? xagmanOwnerRequestedItems
                .Where(entry => entry.ItemId > 0)
                .Select(entry => new XagmanTradeRequestEntry
                {
                    ItemId = entry.ItemId,
                    ItemName = entry.ItemName,
                    IsHq = entry.IsHq,
                    Mode = entry.Mode,
                    Quantity = entry.Quantity,
                    TargetQuantity = entry.TargetQuantity,
                    CurrentQuantity = entry.CurrentQuantity,
                })
                .ToList()
            : new List<XagmanTradeRequestEntry>();
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
                ActiveCharacter = activeKey,
                PreferredTonyCharacter = preferredTony,
                MeetWorld = role == XagmanRole.Tony
                    ? plugin.Configuration.XagmanTargetWorld
                    : (xagmanRunning ? xagmanActiveMeetWorld : string.Empty),
                MeetAetheryte = role == XagmanRole.Tony
                    ? plugin.Configuration.XagmanTargetAetheryte
                    : (xagmanRunning ? xagmanActiveMeetAetheryte : string.Empty),
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
                ItemIds = items.Select(item => item.ItemId).Distinct().ToList(),
                RequestedItems = requestedItems,
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

     private List<XagmanPeerPresence> GetXagmanQueueForTony(string tonyCharacter)
     {
         return plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled)
             .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
             .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.QueueRequestedAtUtc > DateTime.MinValue)
             .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                 || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                 || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
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
             .Where(peer => peer.QueueRequestedAtUtc > DateTime.MinValue)
             .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                 || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                 || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
             .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.Status is XagmanStatus.Standby or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.ReadyForQueue or XagmanStatus.Paused or XagmanStatus.Called or XagmanStatus.Trading)
             .OrderBy(peer => GetXagmanTradeTurnPriority(peer.Status))
             .ThenBy(peer => peer.QueueRequestedAtUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .ToList();
     }

     private bool HasXagmanLocalTradeTurn(string characterNameWorld)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner || string.IsNullOrWhiteSpace(characterNameWorld))
            return false;
        if (xagmanQueueRequestedAtUtc == DateTime.MinValue)
            return false;
        var localPriority = GetXagmanTradeTurnPriority(xagmanStatus);
        if (localPriority >= 10)
            return false;
        var tonyCharacter = GetXagmanLockedTonyCharacter();
        var peers = GetXagmanTradeTurnPeersForTony(tonyCharacter)
            .Select(peer => (Priority: GetXagmanTradeTurnPriority(peer.Status), peer.QueueRequestedAtUtc, peer.ActiveCharacter, peer.InstanceId))
            .ToList();
        peers.Add((localPriority, xagmanQueueRequestedAtUtc, characterNameWorld, plugin.InstanceId));
        var ordered = peers
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.QueueRequestedAtUtc)
            .ThenBy(entry => entry.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered.Count > 0 && ordered[0].InstanceId.Equals(plugin.InstanceId, StringComparison.OrdinalIgnoreCase);
    }

     private XagmanPeerPresence? GetXagmanInFlightOwnerForTony(string tonyCharacter)
    {
        return GetXagmanTradeTurnPeersForTony(tonyCharacter)
            .FirstOrDefault(peer => peer.Status is XagmanStatus.Called or XagmanStatus.Trading);
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
            && !HasXagmanOwnerCollectionItemsRemaining(plugin.Configuration.XagmanItems, characterNameWorld);
    }

     private bool IsXagmanOwnerCalled(string characterNameWorld)
     {
         if (!HasXagmanLocalTradeTurn(characterNameWorld))
             return false;
         var preferredTony = GetXagmanLockedTonyCharacter();
         var tonyPeer = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
             .Where(peer => string.IsNullOrWhiteSpace(preferredTony) || peer.ActiveCharacter.Equals(preferredTony, StringComparison.OrdinalIgnoreCase))
             .FirstOrDefault(peer => peer.ActiveTradePartner.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase));
         if (tonyPeer == null)
             return false;
         xagmanPreferredTonyCharacter = tonyPeer.ActiveCharacter;
         xagmanActiveTradePartner = tonyPeer.ActiveCharacter;
         xagmanActiveTradePartnerInstanceId = tonyPeer.InstanceId;
         xagmanTonyMode = tonyPeer.TonyMode;
         if (ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(characterNameWorld))
             TryRequireXagmanReceiverAutoAccept($"owner {characterNameWorld} pending Tony supply");
         return true;
     }
     private XagmanTonyMode GetXagmanActiveTonyMode()
     {
         var preferredTony = string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter) ? GetXagmanPreferredTonyCharacter() : xagmanPreferredTonyCharacter;
         var liveTony = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
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
                 var success = TrySetXagmanDropboxAutoAccept(enabled);
                 if (requireSuccess && !success)
                 {
                     plugin.TaskRunner.AddLog($"Xagman: failed to {(enabled ? "enable" : "disable")} Dropbox auto-accept trades for {label}.");
                     onFailure?.Invoke();
                 }
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
         if (plugin.DropboxQueue.TryClearQueue(out var message))
             return;

         plugin.TaskRunner.AddLog($"Xagman: {message}");
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
         TrySetXagmanDropboxAutoAccept(false);
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

     private bool TrySetXagmanDropboxAutoAccept(bool enabled)
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

     private bool TryRequireXagmanReceiverAutoAccept(string contextLabel)
    {
        if (TrySetXagmanDropboxAutoAccept(true))
            return true;

        plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(contextLabel)
            ? "Xagman: failed to enable Dropbox auto-accept on the receiving side."
            : $"Xagman: failed to enable Dropbox auto-accept for {contextLabel}.");
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
                return true;
            }

            var property = config.GetType().GetProperty(propertyName, bindingFlags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(config, value);
                return true;
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
        var tonyCharacter = GetXagmanActiveTonyCharacterForGilRequests();
        var tonyGilMinimum = GetXagmanEffectiveTonyGilMinimum(tonyCharacter);
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
        foreach (var item in items)
        {
            if (item.Mode == XagmanItemMode.Give)
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            var tonyTradableQuantity = IsXagmanGilItem(item.ItemId)
                ? GetXagmanCharacterTradableQuantity(tonyCharacter, item.ItemId, item.IsHq, item.ItemName, tonyGilMinimum)
                : 0;
            if (item.Mode == XagmanItemMode.Take)
            {
                var requestedQuantity = Math.Max(0, item.Quantity);
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
                    TargetQuantity = Math.Max(0, item.Quantity),
                    CurrentQuantity = currentQuantity,
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
        foreach (var item in items)
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
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        return requests
            .Where(entry => entry.ItemId > 0)
            .Select(entry => new XagmanTradeRequestEntry
            {
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
     }

     private static List<XagmanTradeRequestEntry> CloneXagmanTradeRequests(IEnumerable<XagmanTradeRequestEntry> requests)
    {
        return requests
            .Where(entry => entry.ItemId > 0 && !string.IsNullOrWhiteSpace(entry.ItemName))
            .Select(entry => new XagmanTradeRequestEntry
            {
                ItemId = entry.ItemId,
                ItemName = entry.ItemName,
                IsHq = entry.IsHq,
                Mode = entry.Mode,
                Quantity = entry.Quantity,
                TargetQuantity = entry.TargetQuantity,
                CurrentQuantity = entry.CurrentQuantity,
            })
            .ToList();
    }

     private static string GetXagmanTradeSnapshotKey(uint itemId, bool isHq)
    {
        return $"{itemId}:{(isHq ? 1 : 0)}";
    }

     private bool HasXagmanOwnerCollectionTradeCompleted(IReadOnlyList<XagmanItemEntry> items, string ownerCharacter)
    {
        foreach (var item in items)
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
        foreach (var request in requests)
        {
            if (request.ItemId == 0)
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, request.ItemId, request.IsHq, request.ItemName);
            if (currentQuantity > request.CurrentQuantity)
                return true;
            if (request.Mode is XagmanItemMode.Balance or XagmanItemMode.TopUp
                && request.TargetQuantity > 0
                && currentQuantity >= request.TargetQuantity)
                return true;
        }
        return false;
    }

     private void SetXagmanOwnerRequestedItems(IReadOnlyList<XagmanTradeRequestEntry> requests, bool logRequests = true)
    {
        xagmanOwnerRequestedItems.Clear();
        xagmanOwnerRequestedItems.AddRange(CloneXagmanTradeRequests(requests));
        if (logRequests)
        {
            var requestUnits = xagmanOwnerRequestedItems.Sum(item => Math.Max(0, item.Quantity));
            var allAvailableRequests = xagmanOwnerRequestedItems.Count(item => item.Mode == XagmanItemMode.Take && item.Quantity <= 0);
            foreach (var request in xagmanOwnerRequestedItems)
            {
                plugin.TaskRunner.AddLog($"Xagman: request {GetXagmanTradeRequestLabel(request)} <= {GetXagmanTradeRequestAmountLabel(request)} from Tony (mode={request.Mode}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
            }
            plugin.TaskRunner.AddLog($"Xagman: requested {xagmanOwnerRequestedItems.Count} Tony supply entr{(xagmanOwnerRequestedItems.Count == 1 ? "y" : "ies")} totaling {requestUnits} units{(allAvailableRequests > 0 ? $" + {allAvailableRequests} all-available request(s)" : string.Empty)}.");
        }
        PublishXagmanPresence();
    }

     private int QueueXagmanOwnerCollectionItems(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var ownerGiveItems = items
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
            var limitLabel = GetXagmanTradeLimitLabel(item);
            var quantity = item.Mode switch
            {
                XagmanItemMode.Give => item.Quantity <= 0 ? localAvailable : Math.Min(localAvailable, item.Quantity),
                XagmanItemMode.Balance => Math.Max(0, localAvailable - Math.Max(0, item.Quantity)),
                _ => 0,
            };
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => 0 (mode={item.Mode}, limit={limitLabel}, local={localAvailable}, partner=0).");
                continue;
            }
            plugin.IpcClient.DropboxSetItemQuantity(item.ItemId, item.IsHq, quantity);
            queuedEntries++;
            queuedUnits += quantity;
            plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => {quantity} (mode={item.Mode}, limit={limitLabel}, local={localAvailable}, partner=0).");
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
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var item in items)
        {
            var quantity = GetXagmanTradeQuantity(item, localCharacter, partnerCharacter, out var localAvailable, out var partnerAvailable);
            var itemLabel = GetXagmanTradeItemLabel(item);
            var limitLabel = GetXagmanTradeLimitLabel(item);
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => 0 (mode={item.Mode}, limit={limitLabel}, local={localAvailable}, partner={partnerAvailable}).");
                continue;
            }
            plugin.IpcClient.DropboxSetItemQuantity(item.ItemId, item.IsHq, quantity);
            queuedEntries++;
            queuedUnits += quantity;
            plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => {quantity} (mode={item.Mode}, limit={limitLabel}, local={localAvailable}, partner={partnerAvailable}).");
        }
        plugin.TaskRunner.AddLog($"Xagman: queued {queuedEntries}/{items.Count} item entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} units.");
     }

     private void QueueXagmanRequestedSupplyItems(IReadOnlyList<XagmanTradeRequestEntry> requests)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var request in requests)
        {
            var localAvailable = GetXagmanCharacterItemQuantity(localCharacter, request.ItemId, request.IsHq, request.ItemName);
            var localTradableQuantity = GetXagmanCharacterTradableQuantity(localCharacter, request.ItemId, request.IsHq, request.ItemName);
            var requestedQuantity = request.Quantity <= 0 ? localTradableQuantity : request.Quantity;
            var quantity = Math.Min(localTradableQuantity, requestedQuantity);
            var requestLabel = GetXagmanTradeRequestLabel(request);
            var requestAmountLabel = GetXagmanTradeRequestAmountLabel(request);
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: supply {requestLabel} => 0 (requested={requestAmountLabel}, mode={request.Mode}, local={localAvailable}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
                continue;
            }
            plugin.IpcClient.DropboxSetItemQuantity(request.ItemId, request.IsHq, quantity);
            queuedEntries++;
            queuedUnits += quantity;
            plugin.TaskRunner.AddLog($"Xagman: supply {requestLabel} => {quantity} (requested={requestAmountLabel}, mode={request.Mode}, local={localAvailable}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
        }
        plugin.TaskRunner.AddLog($"Xagman: Tony queued {queuedEntries}/{requests.Count} requested supply entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} units.");
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
        return request.IsHq ? $"{request.ItemName} HQ" : request.ItemName;
    }

    private static string GetXagmanTradeLimitLabel(XagmanItemEntry item)
    {
        return item.Quantity <= 0 ? "all" : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetXagmanTradeRequestAmountLabel(XagmanTradeRequestEntry request)
    {
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

    private unsafe int GetXagmanLiveLocalItemQuantity(uint itemId, bool isHq)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 0;
            if (IsXagmanGilItem(itemId))
                return (int)inventoryManager->GetGil();
            return inventoryManager->GetInventoryItemCount(itemId, isHq);
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
        var query = string.IsNullOrWhiteSpace(itemName)
            ? itemId.ToString(CultureInfo.InvariantCulture)
            : itemName;
        return SearchXagmanCharacterMatches(query)
            .Where(match => match.CharacterNameWorld.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
            .Where(match => match.ItemId == itemId)
            .Where(match => match.IsHq == isHq)
            .Sum(match => Math.Max(0, match.Quantity));
    }

     private XagmanPeerPresence? GetXagmanLiveTonyPeer()
    {
        var preferredTony = string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            ? GetXagmanPreferredTonyCharacter()
            : xagmanPreferredTonyCharacter;
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
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
        if (!HasXagmanLocalTradeTurn(characterNameWorld))
            return false;
        var tonyPeer = GetXagmanLiveTonyPeer();
        if (tonyPeer == null)
            return false;
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)
            && !tonyPeer.InstanceId.Equals(xagmanActiveTradePartnerInstanceId, StringComparison.OrdinalIgnoreCase))
            return false;
        return tonyPeer.ActiveTradePartner.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase);
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
        TrySetXagmanDropboxAutoAccept(false);
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
                    TrySetXagmanDropboxAutoAccept(false);
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

        var hasAlternateTony = xagmanTonyRunList.Any(key => !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase));
        if (!hasAlternateTony)
        {
            plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(activePartner)
                ? $"Xagman: Tony {xagmanActiveCharacter} has no alternate Tony remaining after a standby rotation request; finalizing with warning summary."
                : $"Xagman: owner {activePartner} requested Tony rotation after trade failure, but Tony {xagmanActiveCharacter} has no alternate Tony remaining; finalizing with warning summary.");
            StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: false, completedWithWarnings: true, broadcastPeerCompletion: true);
            return;
        }

        xagmanStatus = XagmanStatus.ReturningHome;
        xagmanStatusText = string.IsNullOrWhiteSpace(activePartner)
            ? $"Tony {xagmanActiveCharacter} is rotating to the next Tony."
            : $"Tony {xagmanActiveCharacter} is rotating after {activePartner} entered standby.";
        plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(activePartner)
            ? $"Xagman: Tony {xagmanActiveCharacter} is rotating after a standby request."
            : $"Xagman: owner {activePartner} requested Tony rotation after trade failure; rotating Tony {xagmanActiveCharacter}.");
        RotateXagmanTony();
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
