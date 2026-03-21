using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using SysAction = System.Action;
using Dalamud.Bindings.ImGui;
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
    private bool xagmanObservedDropboxBusy;
    private DateTime xagmanQueueRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanLastPresencePublishUtc = DateTime.MinValue;
    private DateTime xagmanLastTonyActionAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyRunStartedAtUtc = DateTime.MinValue;
    private int xagmanCurrentTonyIndex = -1;
    private List<string> xagmanTonyRunList = new();
    private List<string>? xagmanAetheryteNames;
    private List<XagmanItemSearchEntry> xagmanItemResults = new();
    private static readonly string[] xagmanItemModeLabels = { "Give", "Take", "Balance" };
    private static readonly JsonSerializerOptions xagmanItemListJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private sealed class XagmanItemSearchEntry
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
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
        public HashSet<uint> ItemIds { get; init; } = new();
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
        xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort <= 0 ? cfg.XagmanHubPort : xagmanPendingHubPort);
        ImGui.TextColored(new Vector4(0.8f, 0.6f, 1.0f, 1.0f), "Xagman");
        ImGui.TextDisabled("Cross-client FC trade coordination with Tony and Franchise Owner roles using XA Slave peer presence.");
        ImGui.Spacing();
        var arOk = plugin.IpcClient.IsAutoRetainerAvailable();
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        var xaDbOk = plugin.IpcClient.IsXaDatabaseAvailable();
        var dbxOk = plugin.IpcClient.IsDropboxAvailable();
        var vnavOk = plugin.IpcClient.VnavIsReady();
        var viwiOk = plugin.IpcClient.IsViwiAvailable();
        var allRequired = arOk && lsOk && xaDbOk && dbxOk && vnavOk && viwiOk;
        DrawTaskPluginStatus(true);
        ImGui.Text("Task Extras: ");
        ImGui.SameLine();
        ImGui.TextColored(dbxOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), dbxOk ? "[Dropbox]" : "[Dropbox ✗]");
        ImGui.SameLine();
        ImGui.TextColored(viwiOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), viwiOk ? "[VIWI]" : "[VIWI ✗]");
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
        ImGui.SameLine();
        ImGui.TextDisabled($"Hub: 127.0.0.1:{plugin.XagmanPeers.HubPort}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Port##xagmanPort", ref xagmanPendingHubPort))
            xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort);
        ImGui.SameLine();
        if (ImGui.Button("Apply##xagmanPort"))
            xagmanPendingHubPort = plugin.ApplyXagmanHubPort(xagmanPendingHubPort);
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? cfg.XagmanRole == XagmanRole.Tony
                    ? "Import all AutoRetainer characters into the Tony list."
                    : "Import all AutoRetainer characters into the Franchise Owner list."
                : "AutoRetainer config file was not found.");
        ImGui.SameLine();
        if (!xaDbOk) ImGui.BeginDisabled();
        if (ImGui.Button("Pull XA Database Info##xagmanPullXa"))
        {
            PullXaDatabaseInfo();
            ClearXagmanMatchingSelectionCaches();
        }
        if (!xaDbOk) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbOk ? "Refresh cached character info used by the Xagman tables and item matching." : "XA Database must be loaded for this button.");
        if (cfg.XagmanRole == XagmanRole.FranchiseOwner)
        {
            DrawXagmanWorldSelector(cfg);
            ImGui.SameLine();
            DrawXagmanAetheryteSelector(cfg);
            ImGui.Spacing();
            if (!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld))
                ImGui.TextDisabled($"Meet Destination: {GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)}");
            else
                ImGui.TextDisabled("Meet Destination: not set");
        }
        else
        {
            var meetWorld = xagmanRunning && xagmanActiveRole == XagmanRole.Tony ? xagmanActiveMeetWorld : string.Empty;
            var meetAetheryte = xagmanRunning && xagmanActiveRole == XagmanRole.Tony ? xagmanActiveMeetAetheryte : string.Empty;
            if (string.IsNullOrWhiteSpace(meetWorld))
                TryGetXagmanMeetDestinationForTony(GetXagmanPreferredTonyCharacter(), out meetWorld, out meetAetheryte);
            ImGui.Spacing();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(meetWorld)
                ? "Meet Destination: waiting for Franchise Owner relay"
                : $"Meet Destination: {GetPrepLogisticsDestinationLabel(meetWorld, meetAetheryte)}");
        }
        ImGui.Spacing();
        var autoReturnToFc = cfg.XagmanAutoReturnToFc;
        if (ImGui.Checkbox("Return to FC when finished##xagmanReturnFc", ref autoReturnToFc))
        {
            cfg.XagmanAutoReturnToFc = autoReturnToFc;
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
        var canStartTony = !xagmanRunning && selectedTonyChars.Count > 0 && allRequired;
        var canStartOwners = !xagmanRunning && selectedFranchiseChars.Count > 0 && !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && allRequired && cfg.XagmanTonyCharacters.Count > 0;
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                $"Start Tony ({selectedTonyChars.Count})##xagmanTonyStart",
                canStartTony,
                StartXagmanTonyTask,
                !allRequired
                    ? "Missing required plugins. Check the plugin status above."
                    : "Select at least one Tony character.");
            if (started)
                xagmanShowLog = true;
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
                StartXagmanFranchiseTask,
                !allRequired
                    ? "Missing required plugins. Check the plugin status above."
                    : cfg.XagmanTonyCharacters.Count == 0
                        ? "Add at least one Tony character first."
                        : string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld)
                            ? "Select a meet world first."
                            : "Select at least one Franchise Owner character.");
            if (started)
                xagmanShowLog = true;
        }
        if (xagmanRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"State: {xagmanStatus} — {xagmanStatusText}");
        }
        ImGui.Spacing();
        if (cfg.XagmanRole == XagmanRole.Tony)
            DrawXagmanTonyTable(cfg);
        else
            DrawXagmanFranchiseTable(cfg);
        ImGui.Spacing();
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
        if (ImGui.Checkbox("Logout on completion##xagmanLogout", ref logoutOnComplete))
        {
            cfg.XagmanLogoutOnComplete = logoutOnComplete;
            cfg.Save();
        }
        ImGui.SameLine();
        var enableArMultiOnComplete = cfg.XagmanEnableArMultiOnComplete;
        if (ImGui.Checkbox("Enable AR Multi Mode on completion##xagmanArMulti", ref enableArMultiOnComplete))
        {
            cfg.XagmanEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
        DrawTaskLog("xagman", ref xagmanShowLog, runner);
    }

    private void DrawXagmanTonyTable(Configuration cfg)
    {
        var chars = cfg.XagmanTonyCharacters;
        var charInfo = cfg.ReloggerCharacterInfo;
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Tonys");
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.SameLine();
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
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##xagmanTonySearch", "Search Tony name or world...", ref xagmanTonySearchFilter, 128);
        if (ImGui.BeginTable("XagmanTonyTable", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, 175)))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 42f);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Inv", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 95f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();
            for (var i = 0; i < chars.Count; i++)
            {
                var entry = chars[i];
                var charName = entry.CharacterNameWorld;
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);
                if (!IsXagmanTonyCharacterVisible(cfg, entry, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = xagmanTonySelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##xagmanTonySel{i}", ref selected))
                {
                    if (selected) xagmanTonySelectedIndices.Add(i);
                    else xagmanTonySelectedIndices.Remove(i);
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(charName);
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
        ImGui.SetNextItemWidth(260);
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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Franchise Owners");
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.SameLine();
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
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##xagmanOwnerSearch", "Search owner name or world...", ref xagmanFranchiseSearchFilter, 128);
        if (ImGui.BeginTable("XagmanOwnerTable", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, 175)))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 42f);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region/DC", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Inv", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 95f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();
            for (var i = 0; i < chars.Count; i++)
            {
                var charName = chars[i];
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);
                if (!IsXagmanFranchiseCharacterVisible(cfg, charName, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = xagmanFranchiseSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##xagmanOwnerSel{i}", ref selected))
                {
                    if (selected) xagmanFranchiseSelectedIndices.Add(i);
                    else xagmanFranchiseSelectedIndices.Remove(i);
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(charName);
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
        ImGui.SetNextItemWidth(260);
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
    private void DrawXagmanItemSection(string title, List<XagmanItemEntry> items, string id)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), title);
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button($"Add Item##{id}AddItem"))
            ImGui.OpenPopup($"{id}AddItemPopup");
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.BeginPopup($"{id}AddItemPopup"))
        {
            ImGui.SetNextItemWidth(280f);
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

        ImGui.SameLine();
        if (ImGui.Button($"Gil##{id}Gil"))
            AddXagmanItem(items, 1, "Gil", false);

        ImGui.SameLine();
        if (ImGui.Button($"Lists##{id}Lists"))
            ImGui.OpenPopup($"{id}ListsPopup");

        ImGui.SameLine();
        if (ImGui.Button($"Import##{id}Import"))
        {
            xagmanItemImportJson = ImGui.GetClipboardText();
            if (TryImportXagmanItemList(title, xagmanItemImportJson, items, out var importMessage))
            {
                arImportStatus = importMessage;
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                xagmanItemImportJson = string.Empty;
                ClearXagmanItemSearch();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button($"Export##{id}Export"))
            ExportXagmanItemList(title, items);

        ImGui.SameLine();
        if (ImGui.Button($"Set All##{id}SetAll"))
            ImGui.OpenPopup($"{id}SetAllPopup");

        ImGui.SameLine();
        if (ImGui.Button($"Clear##{id}Clear"))
        {
            items.Clear();
            SaveXagmanSharedItemsState();
            ClearXagmanItemSearch();
        }

        DrawXagmanSavedListsPopup(title, items, id);
        DrawXagmanMassModePopup(items, id);

        if (ImGui.BeginTable($"{id}Table", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 150f)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Amt", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 60f);
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
                ImGui.TableNextColumn();
                var modeIndex = (int)item.Mode;
                ImGui.SetNextItemWidth(80f);
                if (ImGui.Combo($"##{id}Mode{i}", ref modeIndex, xagmanItemModeLabels, xagmanItemModeLabels.Length))
                {
                    item.Mode = (XagmanItemMode)Math.Clamp(modeIndex, 0, xagmanItemModeLabels.Length - 1);
                    SaveXagmanSharedItemsState();
                }
                ImGui.TableNextColumn();
                var qty = item.Quantity;
                ImGui.SetNextItemWidth(60f);
                if (ImGui.InputInt($"##{id}Qty{i}", ref qty))
                {
                    item.Quantity = Math.Max(0, qty);
                    SaveXagmanSharedItemsState();
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
    private void ExportXagmanItemList(string title, IReadOnlyList<XagmanItemEntry> items)
    {
        var package = new XagmanItemListPackage
        {
            ListId = Guid.NewGuid().ToString("N"),
            Title = title,
            ExportedAtUtc = DateTime.UtcNow,
            Items = items
                .Select(item => new XagmanItemEntry
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    IsHq = item.IsHq,
                    Mode = item.Mode,
                    Quantity = Math.Max(0, item.Quantity),
                })
                .ToList(),
        };
        var json = JsonSerializer.Serialize(package, xagmanItemListJsonOptions);
        ImGui.SetClipboardText(json);
        arImportStatus = $"Xagman: copied {title} JSON ({package.ListId}) to clipboard";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }
    private bool TryImportXagmanItemList(string title, string json, List<XagmanItemEntry> items, out string message)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            message = $"Xagman: paste {title} JSON before importing.";
            return false;
        }
        XagmanItemListPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<XagmanItemListPackage>(json, xagmanItemListJsonOptions);
        }
        catch (Exception ex)
        {
            message = $"Xagman: import failed: {ex.Message}";
            return false;
        }
        if (package == null || package.Items == null)
        {
            message = "Xagman: import JSON did not contain a valid item list.";
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
                Mode = group.First().Mode,
                Quantity = Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        var listId = string.IsNullOrWhiteSpace(package.ListId) ? "no-list-id" : package.ListId;
        message = $"Xagman: imported {importedItems.Count} item(s) into {title} from {listId}";
        return true;
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
        if (ImGui.BeginTable("XagmanQueueTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, new Vector2(0, 100f)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 35f);
            ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Requested", ImGuiTableColumnFlags.WidthFixed, 150f);
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
    private void DrawXagmanPeersTable()
    {
        var peers = plugin.XagmanPeers.Peers;
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Peers");
        ImGui.TextDisabled($"{peers.Count} remote peers");
        if (ImGui.BeginTable("XagmanPeersTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, new Vector2(0, 100f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Partner", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();
            foreach (var peer in peers.OrderBy(peer => peer.CharacterName, StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.ProcessId))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(peer.ActiveCharacter) ? peer.CharacterName : peer.ActiveCharacter);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.Role.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.Status.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.CurrentWorld);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.TerritoryName);
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
    private void DrawXagmanWorldSelector(Configuration cfg)
    {
        var label = string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld)
            ? "Select World##xagmanWorldButton"
            : $"{cfg.XagmanTargetWorld}##xagmanWorldButton";
        if (ImGui.Button(label))
            ImGui.OpenPopup("XagmanWorldPopup");
        if (!ImGui.BeginPopup("XagmanWorldPopup"))
            return;
        ImGui.SetNextItemWidth(240f);
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
        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##xagmanAetheryteFilter", "Type a location...", ref xagmanAetheryteFilter, 128);
        ImGui.Separator();
        if (ImGui.Selectable("(World only)", string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)))
        {
            cfg.XagmanTargetAetheryte = string.Empty;
            cfg.Save();
            xagmanAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        foreach (var aetheryte in GetXagmanAetheryteNames())
        {
            if (!string.IsNullOrWhiteSpace(xagmanAetheryteFilter)
                && !aetheryte.Contains(xagmanAetheryteFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ImGui.Selectable(aetheryte, string.Equals(cfg.XagmanTargetAetheryte, aetheryte, StringComparison.OrdinalIgnoreCase)))
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
        QueueXagmanMatchingSelection(XagmanMatchSelectionTarget.FranchiseOwner);
    }
    private static string BuildXagmanMatchingItemsKey(IReadOnlyList<XagmanItemEntry> items)
    {
        return string.Join(",",
            items
                .Where(item => item.ItemId > 1)
                .Select(item => item.ItemId)
                .Distinct()
                .OrderBy(itemId => itemId)
                .Select(itemId => itemId.ToString(CultureInfo.InvariantCulture)));
    }
    private void QueueXagmanMatchingSelection(XagmanMatchSelectionTarget target)
    {
        var cfg = plugin.Configuration;
        var selectionLabel = target == XagmanMatchSelectionTarget.Tony ? "Tony" : "Franchise Owner";
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
        var itemsKey = BuildXagmanMatchingItemsKey(cfg.XagmanItems);
        if (string.IsNullOrWhiteSpace(itemsKey))
        {
            if (target == XagmanMatchSelectionTarget.Tony)
                xagmanTonySelectedIndices.Clear();
            else
                xagmanFranchiseSelectedIndices.Clear();
            xagmanPendingMatchSelection = null;
            arImportStatus = "Xagman: add at least one non-gil item before selecting matching characters.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        if (xagmanMatchingCharacterKeysCache.TryGetValue(itemsKey, out var cachedMatches))
        {
            ApplyXagmanMatchingSelection(target, visibleCharacterKeys, cachedMatches);
            return;
        }
        var queries = cfg.XagmanItems
            .Where(item => item.ItemId > 1 && !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new XagmanMatchQueryRequest
            {
                Query = group.Key,
                ItemIds = group.Select(item => item.ItemId).ToHashSet(),
            })
            .OrderBy(request => request.Query, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queries.Count == 0)
        {
            arImportStatus = "Xagman: add at least one searchable item before selecting matching characters.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        xagmanPendingMatchSelection = new XagmanPendingMatchSelectionRequest
        {
            Target = target,
            ItemsKey = itemsKey,
            VisibleCharacterKeys = visibleCharacterKeys,
            Queries = queries,
        };
        arImportStatus = $"Xagman: matching visible {selectionLabel} characters... (0/{queries.Count})";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
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
                if (!queryRequest.ItemIds.Contains(result.ItemId))
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
    private HashSet<string> GetXagmanMatchingCharacterKeys(IReadOnlyList<XagmanItemEntry> items)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => item.ItemId > 1 && !string.IsNullOrWhiteSpace(item.ItemName)))
        {
            foreach (var result in SearchXagmanCharacterMatches(item.ItemName))
            {
                if (result.ItemId != item.ItemId)
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

    private void DrawXagmanSavedListsPopup(string title, List<XagmanItemEntry> items, string id)
    {
        if (!ImGui.BeginPopup($"{id}ListsPopup"))
            return;
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint($"##{id}SaveListName", "List name...", ref xagmanSavedItemListName, 128);
        ImGui.SameLine();
        if (ImGui.Button($"Save Current##{id}SaveCurrent"))
            SaveXagmanNamedItemList(title, items);
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
                LoadXagmanNamedItemList(saved, items);
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
        ImGui.EndPopup();
    }

    private void SaveXagmanNamedItemList(string title, IReadOnlyList<XagmanItemEntry> items)
    {
        var name = xagmanSavedItemListName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            arImportStatus = $"Xagman: enter a name before saving {title}.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        var saved = plugin.Configuration.XagmanSavedItemLists.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var clonedItems = CloneXagmanItems(items);
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

    private void LoadXagmanNamedItemList(XagmanNamedItemList saved, List<XagmanItemEntry> items)
    {
        items.Clear();
        items.AddRange(CloneXagmanItems(saved.Items));
        SaveXagmanSharedItemsState();
        xagmanSavedItemListName = saved.Name;
        ClearXagmanItemSearch();
        arImportStatus = $"Xagman: loaded list '{saved.Name}'.";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void SaveXagmanSharedItemsState()
    {
        ResetXagmanMatchingCharacterSelection();
        plugin.Configuration.XagmanTonyItems.Clear();
        plugin.Configuration.XagmanFranchiseItems.Clear();
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

    private static List<XagmanItemEntry> CloneXagmanItems(IEnumerable<XagmanItemEntry> items)
    {
        return items
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = group.First().Mode,
                Quantity = Math.Max(0, group.First().Quantity),
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
        if (selected.Count == 0)
            return;
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearLog();
        xagmanShowLog = true;
        xagmanRunning = true;
        xagmanActiveRole = XagmanRole.Tony;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "Standing by for Franchise Owner meetup relay.";
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyRunStartedAtUtc = DateTime.UtcNow;
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        xagmanTonyRunList = selected.Select(entry => entry.CharacterNameWorld).ToList();
        xagmanCurrentTonyIndex = 0;
        xagmanPreferredTonyCharacter = selected[0].CharacterNameWorld;
        StartXagmanTonyStartup(selected[0], true);
    }
    private void StartXagmanFranchiseTask()
    {
        var cfg = plugin.Configuration;
        var selected = GetSelectedXagmanFranchiseCharacters();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) || cfg.XagmanTonyCharacters.Count == 0)
            return;
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearLog();
        xagmanShowLog = true;
        xagmanRunning = true;
        xagmanActiveRole = XagmanRole.FranchiseOwner;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "Standing by for Tony meetup acknowledgement.";
        xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyRunStartedAtUtc = DateTime.MinValue;
        SetXagmanActiveMeetDestination(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte);
        PublishXagmanPresence();
        var steps = BuildXagmanFranchiseSteps(selected);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: OnXagmanFranchiseTaskFinished, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }
    private void StartXagmanTonyStartup(XagmanTonyCharacterEntry entry, bool includePreflight)
    {
        xagmanActiveCharacter = entry.CharacterNameWorld;
        xagmanPreferredTonyCharacter = entry.CharacterNameWorld;
        xagmanTonyMode = entry.Mode;
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        PublishXagmanPresence();
        var steps = BuildXagmanTonyStartupSteps(entry, includePreflight);
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void OnXagmanFranchiseTaskFinished()
    {
        if (!RequestXagmanTonyCompletion())
            return;
        plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            ? "Xagman: sent Tony completion signal."
            : $"Xagman: sent Tony completion signal to {xagmanPreferredTonyCharacter}.");
    }

    private List<TaskStep> BuildXagmanTonyStartupSteps(XagmanTonyCharacterEntry entry, bool includePreflight)
    {
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
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
            IsComplete = () => TryResolveXagmanMeetDestinationForTony(entry.CharacterNameWorld),
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
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Failed to relog Tony {entry.CharacterNameWorld}.";
                xagmanRunning = false;
            });
        AddXagmanTeleportSteps(
            steps,
            "Meet",
            GetXagmanActiveMeetDestinationLabel,
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
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                PrimeXagmanDropbox();
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = $"Failed to travel Tony {entry.CharacterNameWorld} to {GetXagmanActiveMeetDestinationLabel()}.";
                xagmanRunning = false;
            });
        steps.Add(new TaskStep
        {
            Name = $"Xagman Tony Ready: {entry.CharacterNameWorld}",
            OnEnter = () =>
            {
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} is ready for queue processing.");
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = $"Tony {entry.CharacterNameWorld} ready at the meet spot.";
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        return steps;
    }
    private List<TaskStep> BuildXagmanFranchiseSteps(List<string> characters)
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
        runner.TotalItems = characters.Count;
        runner.CompletedItems = 0;
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
            IsComplete = IsXagmanFranchiseStartupReady,
            TimeoutSec = 86400f,
        });
        steps.AddRange(helper.BuildPreFlightOnlySteps(characters, runner));
        for (var i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;
            var relogFailed = false;
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Begin: {charName}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"Xagman: processing owner {charName} ({charIndex}/{charTotal}).");
                    xagmanActiveCharacter = charName;
                    xagmanStatus = XagmanStatus.Relogging;
                    xagmanStatusText = $"Relogging owner {charName}.";
                    if (string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter))
                        TryBindXagmanFranchiseTonyForMeetup();
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
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
                GetXagmanActiveMeetDestinationLabel,
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
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Owner {charName} failed to reach the meet spot.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Approach Tony Wait Spot: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanPreferredTonyCharacter);
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = string.IsNullOrWhiteSpace(partnerName)
                        ? $"Approaching Tony's wait spot for {charName}."
                        : $"Approaching Tony {partnerName} for {charName}.";
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget();
                },
                IsComplete = () => relogFailed || IsCurrentTargetInRange(GetCharacterNameFromKey(xagmanPreferredTonyCharacter)),
                TimeoutSec = 60f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    var partnerName = GetCharacterNameFromKey(xagmanPreferredTonyCharacter);
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
                    if (xagmanQueueRequestedAtUtc == DateTime.MinValue)
                        xagmanQueueRequestedAtUtc = DateTime.UtcNow;
                    xagmanStatus = XagmanStatus.Queued;
                    xagmanStatusText = $"Owner {charName} is queued for {xagmanPreferredTonyCharacter}.";
                    runner.AddLog($"Xagman: waiting for Tony {xagmanPreferredTonyCharacter} to call {charName}.");
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
            steps.Add(new TaskStep
            {
                Name = $"Xagman Approach Tony: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Approaching Tony for {charName}.";
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget();
                },
                IsComplete = () => relogFailed || IsCurrentTargetInRange(GetCharacterNameFromKey(xagmanActiveTradePartner)),
                TimeoutSec = 60f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to reach Tony for {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Open Dropbox: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = $"Owner {charName} is trading with Tony {partnerName}.";
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Open Wait: {charName}", 0.5f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Open Item Tab: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    OpenXagmanDropboxTradeTab();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Item Tab Wait: {charName}", 0.5f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Clear Queue: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    ClearXagmanDropbox();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Clear Wait: {charName}", 0.3f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Queue Items: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    QueueXagmanOwnerCollectionItems(cfg.XagmanItems);
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Queue Wait: {charName}", 0.5f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Retarget: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    TryTargetCharacter(partnerName);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Retarget Wait: {charName}", 0.1f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Focus Target: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    FocusXagmanCurrentTarget();
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Trade Focus Wait: {charName}", 0.15f, () => relogFailed));
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Start: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    StartXagmanDropboxTrade();
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman Trade Wait: {charName}",
                ShouldSkip = () => relogFailed,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => relogFailed || PollXagmanTradeCompletion(),
                TimeoutSec = 240f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Trade timed out for owner {charName}.";
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.FailedCharacters.Add(charName);
                },
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
                        if (relogFailed)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Returning owner {charName} to FC.";
                    },
                    () =>
                    {
                        if (relogFailed)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Owner {charName} return-to-FC attempt finished.";
                    },
                    () =>
                    {
                        if (relogFailed)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = $"Owner {charName} return-to-FC attempt failed to start cleanly.";
                    });
            }
            steps.Add(new TaskStep
            {
                Name = $"Xagman Owner Complete: {charName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = charIndex;
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
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
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }
        steps.Add(new TaskStep
        {
            Name = "Xagman Franchise Summary",
            OnEnter = () =>
            {
                xagmanRunning = false;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = runner.FailedCharacters.Count == 0
                    ? "Franchise Owner run completed."
                    : $"Franchise Owner run finished with {runner.FailedCharacters.Count} failures.";
                runner.SuppressLogoutCancel = !cfg.XagmanLogoutOnComplete;
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (cfg.XagmanLogoutOnComplete)
            MonthlyReloggerTask.AddLogoutOnCompleteSteps(steps, runner);
        if (cfg.XagmanEnableArMultiOnComplete)
        {
            steps.Add(new TaskStep
            {
                Name = "Xagman Enable AR Multi",
                OnEnter = () => plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true),
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay("Xagman AR Enable Cooldown", 1.0f));
         }
         return steps;
     }
     private void AddXagmanRelogSteps(List<TaskStep> steps, string charName, TaskRunner runner, SysAction onEnter, SysAction onReady, SysAction onTimeout)
     {
         var relogFailed = false;
         steps.Add(new TaskStep
         {
             Name = $"Xagman Relog: {charName}",
             OnEnter = () =>
             {
                 onEnter();
                 if (MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase))
                 {
                     runner.AddLog($"Xagman: already on {charName}.");
                     return;
                 }
                 runner.AddLog($"Xagman: relogging to {charName}.");
                 ChatHelper.SendMessage($"/ays relog {charName}");
             },
             IsComplete = () => MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                 && CharacterSafetyHelper.IsCharacterSafeWaitReady(),
             TimeoutSec = 120f,
             MaxRetries = 2,
             OnTimeout = () =>
             {
                 relogFailed = true;
                 onTimeout();
             },
         });
         foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Xagman Relog SafeWait ({charName})", 30f))
         {
             var originalComplete = safeWait.IsComplete;
             steps.Add(new TaskStep
             {
                 Name = safeWait.Name,
                 ShouldSkip = () => relogFailed,
                 OnEnter = safeWait.OnEnter,
                 IsComplete = () => relogFailed || (MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase) && originalComplete()),
                 TimeoutSec = safeWait.TimeoutSec,
                 MaxRetries = safeWait.MaxRetries,
                 OnTimeout = () =>
                 {
                     relogFailed = true;
                     onTimeout();
                 },
             });
         }
         steps.Add(new TaskStep
         {
             Name = $"Xagman Relog Ready: {charName}",
             ShouldSkip = () => relogFailed,
             OnEnter = onReady,
             IsComplete = () => true,
             TimeoutSec = 1f,
         });
     }
     private void AddXagmanTeleportSteps(List<TaskStep> steps, string label, Func<string> commandProvider, TaskRunner runner, Func<bool>? alreadyThere, bool allowNoBusy, SysAction onEnter, SysAction onReady, SysAction onTimeout)
     {
         var skipTeleport = false;
         var sawBusy = false;
         var teleportFailed = false;
         steps.Add(new TaskStep
         {
             Name = $"Xagman Teleport {label}: Command",
             OnEnter = () =>
             {
                 onEnter();
                 sawBusy = false;
                 skipTeleport = alreadyThere?.Invoke() ?? false;
                 teleportFailed = false;
                 if (!skipTeleport)
                     plugin.IpcClient.LifestreamExecuteCommand(commandProvider());
             },
             IsComplete = () => true,
             TimeoutSec = 2f,
         });
         steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman Teleport {label}: Init Wait", 1.0f, () => skipTeleport || teleportFailed));
         steps.Add(new TaskStep
         {
             Name = $"Xagman Teleport {label}: Wait Start",
             ShouldSkip = () => skipTeleport || teleportFailed,
             IsComplete = () =>
             {
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
             TimeoutSec = allowNoBusy ? 5f : 12f,
             OnTimeout = () =>
             {
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
             ShouldSkip = () => teleportFailed || (skipTeleport && !sawBusy),
             IsComplete = () => teleportFailed || !plugin.IpcClient.LifestreamIsBusy(),
             TimeoutSec = 60f,
             OnTimeout = () =>
             {
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
                 ShouldSkip = () => teleportFailed,
                 OnEnter = safeWait.OnEnter,
                 IsComplete = () => teleportFailed || originalComplete(),
                 TimeoutSec = safeWait.TimeoutSec,
                 MaxRetries = safeWait.MaxRetries,
                 OnTimeout = () =>
                 {
                     teleportFailed = true;
                     onTimeout();
                 },
             });
         }
         steps.Add(new TaskStep
         {
             Name = $"Xagman Teleport {label}: Ready",
             ShouldSkip = () => teleportFailed,
             OnEnter = onReady,
             IsComplete = () => true,
             TimeoutSec = 1f,
         });
     }
     private void UpdateXagmanFrameworkTick()
     {
         ProcessXagmanPendingMatchSelection();
         if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony && !plugin.TaskRunner.IsRunning)
             UpdateXagmanTonyRuntime();
         var publishInterval = xagmanRunning ? 1.0 : 5.0;
         if ((DateTime.UtcNow - xagmanLastPresencePublishUtc).TotalSeconds >= publishInterval)
             PublishXagmanPresence();
     }
     private void UpdateXagmanTonyRuntime()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony)
            return;
        var queue = GetXagmanQueueForTony(xagmanActiveCharacter);
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
                ClearXagmanDropbox();
                plugin.TaskRunner.AddLog($"Xagman: completed trade with {xagmanActiveTradePartner}.");
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
            }
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is ready for the next owner.";
        }
        if (TryReleaseXagmanTonyStalePartner())
        {
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is ready for the next owner.";
        }
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
        {
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} has called {xagmanActiveTradePartner}.";
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
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} sees {queue.Count} owner(s) in queue.";
        if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
            return;
        xagmanActiveTradePartner = next.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = next.InstanceId;
        xagmanStatus = XagmanStatus.Called;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} called {next.ActiveCharacter}.";
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        plugin.TaskRunner.AddLog($"Xagman: Tony called {next.ActiveCharacter}.");
        if (requestedItems.Count > 0)
            plugin.TaskRunner.AddLog($"Xagman: {next.ActiveCharacter} requested {requestedItems.Count} Tony supply item entr{(requestedItems.Count == 1 ? "y" : "ies")}.");
        if (requestedItems.Count > 0)
            StartXagmanTonyTrade(requestedItems);
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
        ClearXagmanDropbox();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        plugin.TaskRunner.AddLog($"Xagman: released stale active owner {activePartner}.");
        return true;
    }

     private bool TryStartXagmanTonyCompletion()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return false;
        var completionRequester = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => string.IsNullOrWhiteSpace(xagmanActiveCharacter)
                || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                || peer.PreferredTonyCharacter.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.TonyCompletionRequestedAtUtc > DateTime.MinValue && peer.TonyCompletionRequestedAtUtc >= xagmanTonyRunStartedAtUtc)
            .OrderByDescending(peer => peer.TonyCompletionRequestedAtUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
        if (completionRequester == null)
            return false;
        var hasActiveOwners = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => string.IsNullOrWhiteSpace(xagmanActiveCharacter)
                || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                || peer.PreferredTonyCharacter.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase))
            .Any(peer => peer.XagmanEnabled);
        if (hasActiveOwners)
            return false;
        StartXagmanTonyCompletionTask(completionRequester.ActiveCharacter);
        return true;
    }

     private void StartXagmanTonyCompletionTask(string requestedBy)
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
                ClearXagmanDropbox();
                xagmanRunning = false;
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                xagmanQueueRequestedAtUtc = DateTime.MinValue;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = string.IsNullOrWhiteSpace(requestedBy)
                    ? $"Tony {tonyCharacter} completed."
                    : $"Tony {tonyCharacter} completed after {requestedBy} finished.";
                runner.AddLog(string.IsNullOrWhiteSpace(requestedBy)
                    ? $"Xagman: Tony {tonyCharacter} received completion signal."
                    : $"Xagman: Tony {tonyCharacter} received completion signal from {requestedBy}.");
                runner.SuppressLogoutCancel = !cfg.XagmanLogoutOnComplete;
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (cfg.XagmanLogoutOnComplete)
            MonthlyReloggerTask.AddLogoutOnCompleteSteps(steps, runner);
        if (cfg.XagmanEnableArMultiOnComplete)
        {
            steps.Add(new TaskStep
            {
                Name = "Xagman Enable AR Multi",
                OnEnter = () => plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true),
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay("Xagman AR Enable Cooldown", 1.0f));
        }
        plugin.TaskRunner.Start("Xagman", steps, onFinished: StopXagmanTask, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

     private void StartXagmanTonyTrade(IReadOnlyList<XagmanTradeRequestEntry>? requestedItems = null)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning || string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return;
        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
        var supplyRequests = requestedItems == null
            ? new List<XagmanTradeRequestEntry>()
            : requestedItems
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
        var usingSupplyRequests = supplyRequests.Count > 0;
        var items = plugin.Configuration.XagmanItems.ToList();
        if (!usingSupplyRequests && items.Count == 0)
        {
            plugin.TaskRunner.AddLog("Xagman: shared item list is empty, skipping trade.");
            return;
        }
        if (usingSupplyRequests)
            plugin.TaskRunner.AddLog($"Xagman: Tony will supply {supplyRequests.Count} requested item entr{(supplyRequests.Count == 1 ? "y" : "ies")} to {partnerName}.");
        var steps = new List<TaskStep>
        {
            new()
            {
                Name = $"Xagman Target {partnerName}",
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Targeting {partnerName}.";
                    TryTargetCharacter(partnerName);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                Name = $"Xagman Approach {partnerName}",
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = $"Approaching {partnerName}.";
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget();
                },
                IsComplete = () => IsCurrentTargetInRange(partnerName),
                TimeoutSec = 60f,
                OnTimeout = () =>
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Failed to reach {partnerName}.";
                },
            },
            new()
            {
                Name = $"Xagman Tony Trade Open Dropbox {partnerName}",
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = $"Trading with {partnerName}.";
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Open Wait {partnerName}", 0.5f),
            new()
            {
                Name = $"Xagman Tony Trade Open Item Tab {partnerName}",
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Item Tab Wait {partnerName}", 0.5f),
            new()
            {
                Name = $"Xagman Tony Trade Clear Queue {partnerName}",
                OnEnter = ClearXagmanDropbox,
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Clear Wait {partnerName}", 0.3f),
            new()
            {
                Name = $"Xagman Tony Trade Queue Items {partnerName}",
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
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Queue Wait {partnerName}", 0.5f),
            new()
            {
                Name = $"Xagman Tony Trade Retarget {partnerName}",
                OnEnter = () => TryTargetCharacter(partnerName),
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Retarget Wait {partnerName}", 0.1f),
            new()
            {
                Name = $"Xagman Tony Trade Focus Target {partnerName}",
                OnEnter = FocusXagmanCurrentTarget,
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay($"Xagman Tony Trade Focus Wait {partnerName}", 0.15f),
            new()
            {
                Name = $"Xagman Tony Trade Start {partnerName}",
                OnEnter = () =>
                {
                    StartXagmanDropboxTrade();
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            },
            new()
            {
                Name = $"Xagman Tony Trade Wait {partnerName}",
                OnEnter = () => xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy(),
                IsComplete = PollXagmanTradeCompletion,
                TimeoutSec = 240f,
                OnTimeout = () =>
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = $"Trade timed out with {partnerName}.";
                },
            },
            new()
            {
                Name = $"Xagman Tony Trade Finish {partnerName}",
                OnEnter = () =>
                {
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
        };
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

     private void RotateXagmanTony()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || xagmanTonyRunList.Count < 2 || plugin.TaskRunner.IsRunning)
            return;
        xagmanCurrentTonyIndex = (xagmanCurrentTonyIndex + 1) % xagmanTonyRunList.Count;
        var nextKey = xagmanTonyRunList[xagmanCurrentTonyIndex];
        var nextEntry = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(entry => entry.CharacterNameWorld.Equals(nextKey, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = nextKey, Mode = xagmanTonyMode };
        plugin.TaskRunner.AddLog($"Xagman: rotating Tony to {nextEntry.CharacterNameWorld}.");
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        StartXagmanTonyStartup(nextEntry, true);
    }
     private void StopXagmanTask()
     {
         if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
             plugin.TaskRunner.Cancel();
         xagmanRunning = false;
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
         xagmanQueueRequestedAtUtc = DateTime.MinValue;
         xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
         xagmanTonyRunStartedAtUtc = DateTime.MinValue;
         xagmanTonyRunList.Clear();
         xagmanCurrentTonyIndex = -1;
         PublishXagmanPresence();
         UpdatePriorityTaskExternalStatus();
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

     private bool TryGetXagmanMeetDestinationForTony(string tonyCharacter, out string meetWorld, out string meetAetheryte)
     {
         var ownerPeer = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled)
             .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
             .Where(peer => !string.IsNullOrWhiteSpace(peer.MeetWorld))
             .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                 || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                 || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
             .OrderByDescending(peer => !string.IsNullOrWhiteSpace(tonyCharacter)
                 && peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
             .ThenByDescending(peer => peer.LastSeenUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .FirstOrDefault();
         if (ownerPeer == null)
         {
             meetWorld = string.Empty;
             meetAetheryte = string.Empty;
             return false;
         }
         meetWorld = ownerPeer.MeetWorld;
         meetAetheryte = ownerPeer.MeetAetheryte;
         return true;
     }

     private bool TryResolveXagmanMeetDestinationForTony(string tonyCharacter)
     {
         if (!TryGetXagmanMeetDestinationForTony(tonyCharacter, out var meetWorld, out var meetAetheryte))
             return false;
         var destinationChanged = !meetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase)
             || !meetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase);
         SetXagmanActiveMeetDestination(meetWorld, meetAetheryte);
         if (destinationChanged)
             PublishXagmanPresence();
         return !string.IsNullOrWhiteSpace(xagmanActiveMeetWorld);
     }

     private bool TryBindXagmanFranchiseTonyForMeetup()
     {
         if (string.IsNullOrWhiteSpace(xagmanActiveMeetWorld))
             return false;
         var configuredPreferredTony = GetXagmanPreferredTonyCharacter();
         var tonyPeer = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled)
             .Where(peer => peer.Role == XagmanRole.Tony)
             .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
             .Where(peer => peer.MeetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase))
             .Where(peer => peer.MeetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase))
             .OrderByDescending(peer => !string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
                 && peer.ActiveCharacter.Equals(xagmanPreferredTonyCharacter, StringComparison.OrdinalIgnoreCase))
             .ThenByDescending(peer => !string.IsNullOrWhiteSpace(configuredPreferredTony)
                 && peer.ActiveCharacter.Equals(configuredPreferredTony, StringComparison.OrdinalIgnoreCase))
             .ThenByDescending(peer => peer.LastSeenUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .FirstOrDefault();
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

     private bool IsXagmanFranchiseStartupReady()
     {
         if (string.IsNullOrWhiteSpace(xagmanActiveMeetWorld))
             return false;
         return TryBindXagmanFranchiseTonyForMeetup();
     }

     private void PublishXagmanPresence()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        var activeKey = !string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? xagmanActiveCharacter
            : MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        var preferredTony = !string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            ? xagmanPreferredTonyCharacter
            : GetXagmanPreferredTonyCharacter();
        plugin.Configuration.ReloggerCharacterInfo.TryGetValue(activeKey, out var info);
        var currentWorld = local == null ? string.Empty : WorldData.GetById(local.CurrentWorld.RowId)?.Name ?? string.Empty;
        var homeWorld = local == null ? string.Empty : WorldData.GetById(local.HomeWorld.RowId)?.Name ?? string.Empty;
        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        var items = plugin.Configuration.XagmanItems;
        var requestedItems = xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner
            ? BuildXagmanOwnerTradeRequests(items, activeKey)
            : new List<XagmanTradeRequestEntry>();
        var queueNumber = GetXagmanLocalQueueNumber();
        plugin.XagmanPeers.PublishPresence(new XagmanPeerPresence
        {
            InstanceId = plugin.InstanceId,
            ProcessId = plugin.ProcessId,
            LastSeenUtc = DateTime.UtcNow,
            IsLoggedIn = Plugin.PlayerState.IsLoaded && local != null,
            ContentId = Plugin.PlayerState.ContentId,
            CharacterName = local?.Name.ToString() ?? string.Empty,
            HomeWorld = homeWorld,
            CurrentWorld = currentWorld,
            TerritoryId = Plugin.ClientState.TerritoryType,
            TerritoryName = GetCurrentLocationName(),
            XagmanEnabled = xagmanRunning,
            Role = role,
            TonyMode = xagmanTonyMode,
            Status = xagmanRunning ? xagmanStatus : XagmanStatus.Idle,
            StatusText = xagmanRunning ? xagmanStatusText : "Idle",
            ActiveCharacter = activeKey,
            PreferredTonyCharacter = preferredTony,
            MeetWorld = xagmanRunning ? xagmanActiveMeetWorld : string.Empty,
            MeetAetheryte = xagmanRunning ? xagmanActiveMeetAetheryte : string.Empty,
            QueueRequestedAtUtc = xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner ? xagmanQueueRequestedAtUtc : DateTime.MinValue,
            TonyCompletionRequestedAtUtc = xagmanTonyCompletionRequestedAtUtc,
            QueueNumber = queueNumber,
            ActiveTradePartner = xagmanActiveTradePartner,
            ActiveTradePartnerInstanceId = xagmanActiveTradePartnerInstanceId,
            MainInventoryFreeSlots = info?.MainInventoryFreeSlots ?? 0,
            Gil = GetXagmanCharacterGil(activeKey),
            ItemIds = items.Select(item => item.ItemId).Distinct().ToList(),
            RequestedItems = requestedItems,
        });
        xagmanLastPresencePublishUtc = DateTime.UtcNow;
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
     private List<XagmanPeerPresence> GetXagmanQueueForTony(string tonyCharacter)
     {
         return plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled)
             .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
             .Where(peer => peer.QueueRequestedAtUtc > DateTime.MinValue)
             .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                 || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                 || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
             .Where(peer => peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.Queued)
             .OrderBy(peer => peer.QueueRequestedAtUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .ToList();
     }
     private int GetXagmanLocalQueueNumber()
     {
         if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner || xagmanQueueRequestedAtUtc == DateTime.MinValue)
             return 0;
         var tonyCharacter = xagmanPreferredTonyCharacter;
         var peers = GetXagmanQueueForTony(tonyCharacter)
             .Select(peer => (peer.QueueRequestedAtUtc, peer.ActiveCharacter, peer.InstanceId))
             .ToList();
         peers.Add((xagmanQueueRequestedAtUtc, xagmanActiveCharacter, plugin.InstanceId));
         var ordered = peers
             .OrderBy(entry => entry.QueueRequestedAtUtc)
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
        var preferredTony = !string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            ? xagmanPreferredTonyCharacter
            : GetXagmanPreferredTonyCharacter();
        if (string.IsNullOrWhiteSpace(preferredTony))
            return false;
        xagmanPreferredTonyCharacter = preferredTony;
        xagmanTonyCompletionRequestedAtUtc = DateTime.UtcNow;
        PublishXagmanPresence();
        return true;
    }

     private bool IsXagmanOwnerCalled(string characterNameWorld)
     {
         var preferredTony = string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter) ? GetXagmanPreferredTonyCharacter() : xagmanPreferredTonyCharacter;
         var tonyPeer = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
             .Where(peer => string.IsNullOrWhiteSpace(preferredTony) || peer.ActiveCharacter.Equals(preferredTony, StringComparison.OrdinalIgnoreCase))
             .FirstOrDefault(peer => peer.ActiveTradePartner.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase));
         if (tonyPeer == null)
             return false;
         xagmanQueueRequestedAtUtc = DateTime.MinValue;
         xagmanActiveTradePartner = tonyPeer.ActiveCharacter;
         xagmanActiveTradePartnerInstanceId = tonyPeer.InstanceId;
         xagmanTonyMode = tonyPeer.TonyMode;
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
         var expectedZone = GetPrepLogisticsAetheryteZoneName(targetAetheryte);
         var currentLocation = GetCurrentLocationName();
         if (!string.IsNullOrWhiteSpace(expectedZone))
             return currentLocation.Equals(expectedZone, StringComparison.OrdinalIgnoreCase);
         return currentLocation.Equals(targetAetheryte, StringComparison.OrdinalIgnoreCase);
     }

     private void PrimeXagmanDropbox()
     {
         OpenXagmanDropboxWindow();
         OpenXagmanDropboxTradeTab();
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
         ChatHelper.SendMessage("/dbq clear");
     }

     private void StartXagmanDropboxTrade()
     {
         plugin.IpcClient.DropboxBeginTrading();
     }

     private List<XagmanTradeRequestEntry> BuildXagmanOwnerTradeRequests(IReadOnlyList<XagmanItemEntry> items, string ownerCharacter)
    {
        var requests = new List<XagmanTradeRequestEntry>();
        foreach (var item in items)
        {
            if (item.Mode == XagmanItemMode.Give)
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            if (item.Mode == XagmanItemMode.Take)
            {
                requests.Add(new XagmanTradeRequestEntry
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    IsHq = item.IsHq,
                    Mode = item.Mode,
                    Quantity = Math.Max(0, item.Quantity),
                    TargetQuantity = Math.Max(0, item.Quantity),
                    CurrentQuantity = currentQuantity,
                });
                continue;
            }
            var neededQuantity = Math.Max(0, item.Quantity - currentQuantity);
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

     private void QueueXagmanOwnerCollectionItems(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var requestedItems = BuildXagmanOwnerTradeRequests(items, localCharacter);
        var giveRowCount = items.Count(item => item.Mode == XagmanItemMode.Give);
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var item in items.Where(item => item.Mode == XagmanItemMode.Give))
        {
            var localAvailable = GetXagmanCharacterItemQuantity(localCharacter, item.ItemId, item.IsHq, item.ItemName);
            var itemLabel = GetXagmanTradeItemLabel(item);
            var limitLabel = GetXagmanTradeLimitLabel(item);
            var quantity = item.Quantity <= 0 ? localAvailable : Math.Min(localAvailable, item.Quantity);
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
        plugin.TaskRunner.AddLog($"Xagman: queued {queuedEntries}/{giveRowCount} owner give entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} units.");
        if (requestedItems.Count == 0)
            return;
        var requestUnits = requestedItems.Sum(item => Math.Max(0, item.Quantity));
        var allAvailableRequests = requestedItems.Count(item => item.Mode == XagmanItemMode.Take && item.Quantity <= 0);
        foreach (var request in requestedItems)
        {
            plugin.TaskRunner.AddLog($"Xagman: request {GetXagmanTradeRequestLabel(request)} <= {GetXagmanTradeRequestAmountLabel(request)} from Tony (mode={request.Mode}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
        }
        plugin.TaskRunner.AddLog($"Xagman: requested {requestedItems.Count} Tony supply entr{(requestedItems.Count == 1 ? "y" : "ies")} totaling {requestUnits} units{(allAvailableRequests > 0 ? $" + {allAvailableRequests} all-available request(s)" : string.Empty)}.");
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
            var requestedQuantity = request.Quantity <= 0 ? localAvailable : request.Quantity;
            var quantity = Math.Min(localAvailable, requestedQuantity);
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
        return item.Mode switch
        {
            XagmanItemMode.Give => item.Quantity <= 0 ? localAvailable : Math.Min(localAvailable, item.Quantity),
            XagmanItemMode.Balance => Math.Min(localAvailable, Math.Max(0, item.Quantity - partnerAvailable)),
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
     private bool PollXagmanTradeCompletion()
     {
         var busy = plugin.IpcClient.DropboxIsBusy();
         if (busy)
         {
             xagmanObservedDropboxBusy = true;
             return false;
         }
         return xagmanObservedDropboxBusy;
     }

     private void FocusXagmanCurrentTarget()
     {
         ChatHelper.SendMessage("/focustarget");
     }

     private void TryTargetCharacter(string characterName)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName))
             return;
         ChatHelper.SendMessage($"/target \"{visibleCharacterName}\"");
     }
     private bool TryPathToCurrentTarget()
     {
         var local = Plugin.ObjectTable.LocalPlayer;
         var target = local?.TargetObject;
         if (local == null || target == null || !plugin.IpcClient.VnavIsReady())
             return false;
         return plugin.IpcClient.VnavPathfindAndMoveCloseTo(target.Position, false, 0.5f);
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
         var parts = value.Split('@');
         return parts[0];
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
