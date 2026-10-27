using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool floorderShowLog;
    private string floorderAddCityInput = string.Empty;
    private string floorderAddAnnouncementInput = string.Empty;
    private readonly Random floorderRng = new();
    private string floorderError = string.Empty;
    private global::XASlave.Localization.UiMessage? floorderErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private DateTime floorderErrorExpiry = DateTime.MinValue;
    private readonly HashSet<string> floorderExpandedSections = new();

    private static readonly string[] FloorderDefaultCities =
    {
        "Limsa Lominsa Lower Decks", "New Gridania", "Ul'dah - Steps of Nald",
        "Foundation", "Idyllshire", "Rhalgr's Reach",
        "Kugane", "The Doman Enclave",
        "The Crystarium",
        "Old Sharlayan", "Radz-at-Han", "Yedlihmad", "Camp Broken Glass",
        "Anagnorisis", "Sinus Lacrimarum", "Reah Tahra",
        "Tuliyollal", "Wachunpelo", "Ok'hanu", "Iq Br'aax",
        "Solution Nine", "Hhusatahwi", "Leynode Mnemo", "Yyasulani Station",
    };

    private static readonly string[] FloorderChannelOptions = { "/echo", "/shout", "/yell", "/say" };

    private const int FloorderMaxMessageLength = 400;

    private void DrawCityChatFlooder()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var green = new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        var red = new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
        var cyan = new Vector4(0.4f, 0.8f, 1.0f, 1.0f);
        var yellow = new Vector4(1.0f, 0.8f, 0.3f, 1.0f);

        // -- Title --
        ImGui.TextColored(cyan, global::XASlave.Localization.UiLocale.Get("S5C5514B66531FC2E"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD2D3BC7D75A885AF"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB364344A34063978"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Plugin Status --
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("S4588EB2A8F8AB239"));
        ImGui.SameLine();
        ImGui.TextColored(lsOk ? green : red, lsOk ? global::XASlave.Localization.UiLocale.Get("S3D7411ED3FFD4E68") : global::XASlave.Localization.UiLocale.Get("S3C1330608BBB5379"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Run Controls --
        if (runner.IsRunning && runner.CurrentTaskName == "City Chat Flooder")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(yellow, global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", runner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabelDisplay);
            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3")))
                runner.Cancel();
        }
        else
        {
            // Error message (always visible when set)
            if (!string.IsNullOrEmpty(floorderError) && DateTime.UtcNow < floorderErrorExpiry)
            {
                ImGui.TextColored(red, global::XASlave.Localization.UiMessage.Resolve(ref floorderErrorUiMessage, floorderError));
                ImGui.Spacing();
            }

            var worldCount = cfg.FloorderSelectedWorlds.Count;
            var cityCount = cfg.FloorderSelectedCities.Count;
            var canStart = lsOk && worldCount > 0 && cityCount > 0 && cfg.FloorderAnnouncements.Count > 0
                && !runner.IsRunning && Plugin.PlayerState.IsLoaded;

            DrawPriorityTaskActionButton(
                SlaveTask.CityChatFlooder,
                global::XASlave.Localization.UiLocale.Native("S53ABFA1371C8230F"),
                canStart,
                StartCityChatFlooder,
                !lsOk
                    ? global::XASlave.Localization.UiLocale.Get("SD208BB89DADAB760")
                    : worldCount == 0
                        ? global::XASlave.Localization.UiLocale.Get("S0111262CE593953D")
                        : cityCount == 0
                            ? global::XASlave.Localization.UiLocale.Get("SA051DBCBB85B0450")
                            : cfg.FloorderAnnouncements.Count == 0
                                ? global::XASlave.Localization.UiLocale.Get("S800E18D91BF7FE06")
                                : !Plugin.PlayerState.IsLoaded
                                    ? global::XASlave.Localization.UiLocale.Get("S2CB54493D411918F")
                                    : global::XASlave.Localization.UiLocale.Get("SAB859E621D62EDD7"));

            ImGui.SameLine();
            var looping = cfg.FloorderEnableLooping;
            if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S0E7DB37CCB0C5899"), ref looping))
            {
                cfg.FloorderEnableLooping = looping;
                cfg.Save();
            }
            if (looping)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Scale(80f));
                var loopDelay = cfg.FloorderLoopDelayMinutes;
                if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S1F6FA6F69D185E60") + "##loop"), ref loopDelay, 1f, 5f, "%.0f"))
                {
                    if (loopDelay < 1f) loopDelay = 1f;
                    if (loopDelay > 60f) loopDelay = 60f;
                    cfg.FloorderLoopDelayMinutes = loopDelay;
                    cfg.Save();
                }
            }

            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S66B42F7075B44F96", worldCount, cityCount, cfg.FloorderAnnouncements.Count));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Chat Channel + Timing --
        ImGui.TextColored(cyan, global::XASlave.Localization.UiLocale.Get("S14DB43FDA20F537A"));
        ImGui.Spacing();
        var channelIdx = Array.IndexOf(FloorderChannelOptions, cfg.FloorderChatChannel);
        if (channelIdx < 0) channelIdx = 0;
        ImGui.SetNextItemWidth(Scale(120f));
        if (global::XASlave.Localization.UiNative.Combo("##FloorderChannel", ref channelIdx, FloorderChannelOptions, FloorderChannelOptions.Length))
        {
            cfg.FloorderChatChannel = FloorderChannelOptions[channelIdx];
            cfg.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE5F10C5FF5BC2B1B"));

        var waitCity = cfg.FloorderWaitBetweenCities;
        ImGui.SetNextItemWidth(Scale(80f));
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("SE1AA81C2FF748EA4") + "##fl"), ref waitCity, 0.5f, 1.0f, "%.1f"))
        {
            if (waitCity < 1f) waitCity = 1f;
            if (waitCity > 30f) waitCity = 30f;
            cfg.FloorderWaitBetweenCities = waitCity;
            cfg.Save();
        }

        var waitAnn = cfg.FloorderWaitAfterAnnounce;
        ImGui.SetNextItemWidth(Scale(80f));
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("SCCEE93CF2BA74041") + "##fl"), ref waitAnn, 0.5f, 1.0f, "%.1f"))
        {
            if (waitAnn < 0.5f) waitAnn = 0.5f;
            if (waitAnn > 10f) waitAnn = 10f;
            cfg.FloorderWaitAfterAnnounce = waitAnn;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- World Selection Grid --
        ImGui.TextColored(cyan, global::XASlave.Localization.UiLocale.FormatKey("S7A529A5146067A08", cfg.FloorderSelectedWorlds.Count));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SD1EC69E64B9609D0") + "##flworlds")))
        {
            foreach (var w in WorldData.Worlds) cfg.FloorderSelectedWorlds.Add(w.Name);
            cfg.FloorderSelectedWorlds = cfg.FloorderSelectedWorlds.Distinct().ToList();
            cfg.SaveDeferred();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##flworlds")))
        {
            cfg.FloorderSelectedWorlds.Clear();
            cfg.SaveDeferred();
        }
        ImGui.Spacing();

        foreach (var region in WorldData.RegionOrder)
        {
            if (!WorldData.DataCenterOrder.TryGetValue(region, out var dcs)) continue;
            var regionWorlds = WorldData.Worlds.Where(w => w.Region == region).ToList();
            var regionSelected = regionWorlds.Count(w => cfg.FloorderSelectedWorlds.Contains(w.Name));

            var regionKey = $"region_{region}";
            var regionOpen = floorderExpandedSections.Contains(regionKey);
            if (ImGui.ArrowButton($"##fltoggle_{region}", regionOpen ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if (regionOpen) floorderExpandedSections.Remove(regionKey);
                else floorderExpandedSections.Add(regionKey);
            }
            ImGui.SameLine();
            ImGui.Text($"{region} ({regionSelected}/{regionWorlds.Count})");

            if (regionOpen)
            {
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SA52ACE420F2175D0") + $"##fl{region}")))
                {
                    foreach (var w in regionWorlds)
                        if (!cfg.FloorderSelectedWorlds.Contains(w.Name))
                            cfg.FloorderSelectedWorlds.Add(w.Name);
                    cfg.SaveDeferred();
                }
                ImGui.SameLine();
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SDC937B59892604F5") + $"##fl{region}")))
                {
                    foreach (var w in regionWorlds)
                        cfg.FloorderSelectedWorlds.Remove(w.Name);
                    cfg.SaveDeferred();
                }

                using (var imguiScope224 = ImRaii.Table($"FlDCTable_{region}", dcs.Length, ImGuiTableFlags.None))
                if (imguiScope224)
                {
                    foreach (var dc in dcs)
                        ImGui.TableSetupColumn(dc, ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();

                    ImGui.TableNextRow();
                    foreach (var dc in dcs)
                    {
                        ImGui.TableNextColumn();
                        var dcWorlds = regionWorlds.Where(w => w.DataCenter == dc).OrderBy(w => w.Name).ToList();
                        foreach (var w in dcWorlds)
                        {
                            var sel = cfg.FloorderSelectedWorlds.Contains(w.Name);
                            if (ImGui.Checkbox($"{w.Name}##fl{w.Id}", ref sel))
                            {
                                if (sel && !cfg.FloorderSelectedWorlds.Contains(w.Name))
                                    cfg.FloorderSelectedWorlds.Add(w.Name);
                                else if (!sel)
                                    cfg.FloorderSelectedWorlds.Remove(w.Name);
                                cfg.SaveDeferred();
                            }
                        }
                    }

                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- City Selection --
        ImGui.TextColored(cyan, global::XASlave.Localization.UiLocale.FormatKey("S3C48535FAF948451", cfg.FloorderSelectedCities.Count));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SD1EC69E64B9609D0") + "##flcities")))
        {
            foreach (var c in FloorderDefaultCities)
                if (!cfg.FloorderSelectedCities.Contains(c))
                    cfg.FloorderSelectedCities.Add(c);
            cfg.SaveDeferred();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##flcities")))
        {
            cfg.FloorderSelectedCities.Clear();
            cfg.SaveDeferred();
        }
        ImGui.Spacing();

        var citiesOpen = !floorderExpandedSections.Contains("cities_closed"); // open by default
        if (ImGui.ArrowButton("##fltoggle_cities", citiesOpen ? ImGuiDir.Down : ImGuiDir.Right))
        {
            if (citiesOpen) floorderExpandedSections.Add("cities_closed");
            else floorderExpandedSections.Remove("cities_closed");
        }
        ImGui.SameLine();
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("SEE636AD3127B77BC"));

        if (citiesOpen)
        {
            var cols = 3;
            using (var imguiScope287 = ImRaii.Table("FlCityTable", cols, ImGuiTableFlags.None))
            if (imguiScope287)
            {
                for (int c = 0; c < cols; c++)
                    ImGui.TableSetupColumn($"col{c}", ImGuiTableColumnFlags.WidthStretch);

                var perCol = (FloorderDefaultCities.Length + cols - 1) / cols;
                ImGui.TableNextRow();
                for (int c = 0; c < cols; c++)
                {
                    ImGui.TableNextColumn();
                    var start = c * perCol;
                    var end = Math.Min(start + perCol, FloorderDefaultCities.Length);
                    for (int i = start; i < end; i++)
                    {
                        var city = FloorderDefaultCities[i];
                        var sel = cfg.FloorderSelectedCities.Contains(city);
                        if (ImGui.Checkbox($"{city}##flcity", ref sel))
                        {
                            if (sel && !cfg.FloorderSelectedCities.Contains(city))
                                cfg.FloorderSelectedCities.Add(city);
                            else if (!sel)
                                cfg.FloorderSelectedCities.Remove(city);
                            cfg.SaveDeferred();
                        }
                    }
                }

            }
        }

        if (cfg.FloorderCustomCities.Count > 0)
        {
            var customOpen = !floorderExpandedSections.Contains("custom_cities_closed");
            if (ImGui.ArrowButton("##fltoggle_custom", customOpen ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if (customOpen) floorderExpandedSections.Add("custom_cities_closed");
                else floorderExpandedSections.Remove("custom_cities_closed");
            }
            ImGui.SameLine();
            ImGui.Text(global::XASlave.Localization.UiLocale.Get("SA878F9E6FCEAB32F"));

            if (customOpen)
            {
                for (int i = 0; i < cfg.FloorderCustomCities.Count; i++)
                {
                    var city = cfg.FloorderCustomCities[i];
                    var sel = cfg.FloorderSelectedCities.Contains(city);
                    if (ImGui.Checkbox($"{city}##flcust_{i}", ref sel))
                    {
                        if (sel && !cfg.FloorderSelectedCities.Contains(city))
                            cfg.FloorderSelectedCities.Add(city);
                        else if (!sel)
                            cfg.FloorderSelectedCities.Remove(city);
                        cfg.SaveDeferred();
                    }
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, red))
                    {
                        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##flrmcust_{i}")))
                        {
                            cfg.FloorderSelectedCities.Remove(city);
                            cfg.FloorderCustomCities.RemoveAt(i);
                            cfg.Save();
                            break;
                        }
                    }
                }
            }
        }

        ImGui.SetNextItemWidth(Scale(200f));
        var cityEnter = ImGui.InputTextWithHint("##FlAddCity", global::XASlave.Localization.UiLocale.Get("S94F73DAAF712B62B"), ref floorderAddCityInput, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S68C65A76FE30B427") + "##fl")) || cityEnter) && !string.IsNullOrWhiteSpace(floorderAddCityInput))
        {
            var trimmed = floorderAddCityInput.Trim();
            if (!cfg.FloorderCustomCities.Contains(trimmed) && !FloorderDefaultCities.Contains(trimmed))
                cfg.FloorderCustomCities.Add(trimmed);
            if (!cfg.FloorderSelectedCities.Contains(trimmed))
                cfg.FloorderSelectedCities.Add(trimmed);
            cfg.Save();
            floorderAddCityInput = string.Empty;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Announcements --
        ImGui.TextColored(cyan, global::XASlave.Localization.UiLocale.FormatKey("SD64C18A82BBE386E", cfg.FloorderAnnouncements.Count));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S51CBF91B7D716A1C"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S9AC5E23492986568", FloorderMaxMessageLength));
        ImGui.Spacing();
        for (int i = 0; i < cfg.FloorderAnnouncements.Count; i++)
        {
            var msg = cfg.FloorderAnnouncements[i];
            var display = msg.Length > 80 ? msg[..77] + "..." : msg;
            var color = msg.Length > FloorderMaxMessageLength ? red : new Vector4(1f, 1f, 1f, 1f);
            ImGui.TextColored(color, $"  {i + 1}. {display}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S0C478AD14BC6F06B", msg, msg.Length, FloorderMaxMessageLength));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, red))
            {
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##flrma{i}")))
                {
                    cfg.FloorderAnnouncements.RemoveAt(i);
                    cfg.Save();
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(-100);
        var annEnter = ImGui.InputTextWithHint("##FlAddAnn", global::XASlave.Localization.UiLocale.Get("S0126E59E833F941D"), ref floorderAddAnnouncementInput, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        var charCount = floorderAddAnnouncementInput.Length;
        var charColor = charCount > FloorderMaxMessageLength ? red : charCount > FloorderMaxMessageLength * 0.8f ? yellow : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
        ImGui.TextColored(charColor, global::XASlave.Localization.UiLocale.FormatKey("SCBF3C0DF1E8BD4F8", charCount, FloorderMaxMessageLength));
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FD728C66C9A256B") + "##flann")) || annEnter) && !string.IsNullOrWhiteSpace(floorderAddAnnouncementInput))
        {
            var trimmed = floorderAddAnnouncementInput.Trim();
            if (trimmed.Length <= FloorderMaxMessageLength)
            {
                cfg.FloorderAnnouncements.Add(trimmed);
                cfg.Save();
                floorderAddAnnouncementInput = string.Empty;
            }
        }

        ImGui.Spacing();
        if (cfg.FloorderAnnouncements.Count > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, red))
            {
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S1CC6022C287C63D7") + "##flclearann")))
                {
                    cfg.FloorderAnnouncements.Clear();
                    cfg.Save();
                }
            }
        }

        // -- Log --
        DrawTaskLog("flooder", ref floorderShowLog, runner);
    }

    private void StartCityChatFlooder()
    {
        var cfg = plugin.Configuration;
        cfg.Save(); // Persist world/city selections on Start
        var runner = plugin.TaskRunner;
        var orderedWorlds = cfg.FloorderSelectedWorlds
            .OrderBy(w => WorldData.GetSortKey(w))
            .ToList();
        var cityList = cfg.FloorderSelectedCities.ToList();
        var totalCityVisits = orderedWorlds.Count * cityList.Count;

        // -- Region validation --
        // Cross-region travel rules: NA↔NA, EU↔EU, JP↔JP only. Any region can travel to OCE.
        // If selected worlds include regions unreachable from current world, halt.
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var currentWorldName = local?.CurrentWorld.ValueNullable?.Name.ToString() ?? "";
            var currentWorldInfo = WorldData.GetByName(currentWorldName);
            if (currentWorldInfo != null)
            {
                var myRegion = currentWorldInfo.Region;
                var unreachable = new List<string>();
                foreach (var w in orderedWorlds)
                {
                    var targetInfo = WorldData.GetByName(w);
                    if (targetInfo == null) continue;
                    var targetRegion = targetInfo.Region;
                    // Same region = OK. Target is OCE = OK (reachable from anywhere).
                    if (targetRegion == myRegion || targetRegion == "OCE") continue;
                    // Different non-OCE region = unreachable
                    unreachable.Add($"{w} ({targetRegion})");
                }
                if (unreachable.Count > 0)
                {
                    global::XASlave.Localization.UiMessage? msgUiPresentation = null;
                    var msg = global::XASlave.Localization.UiMessage.Store(ref msgUiPresentation, global::XASlave.Localization.UiMessage.Format("SD2DD3525D804AE9A", unreachable.Count, myRegion, string.Join(", ", unreachable.Take(5))));
                    if (unreachable.Count > 5) msg = global::XASlave.Localization.UiMessage.Store(ref msgUiPresentation, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref msgUiPresentation, msg), global::XASlave.Localization.UiMessage.Format("S4F4D2932620104F6", unreachable.Count - 5)));
                    msg = global::XASlave.Localization.UiMessage.Store(ref msgUiPresentation, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref msgUiPresentation, msg), global::XASlave.Localization.UiMessage.Key("SA135DA1123F77342")));
                    floorderError = global::XASlave.Localization.UiMessage.Store(ref floorderErrorUiMessage, global::XASlave.Localization.UiMessage.Capture(ref msgUiPresentation, msg));
                    floorderErrorExpiry = DateTime.UtcNow.AddSeconds(30);
                    runner.AddLog(msg);
                    Plugin.Log.Warning($"[XASlave] City Chat Flooder: {msg}");
                    AutoOpenTaskLogIfVerbose(ref floorderShowLog);
                    return;
                }
            }
        }
        catch { /* proceed if check fails */ }

        var steps = new List<TaskStep>();
        var runContentId = Plugin.PlayerState.ContentId;
        if (runContentId == 0 || !Plugin.PlayerState.IsLoaded)
        {
            runner.AddLog("City Chat Flooder: log into a character before starting.");
            return;
        }

        void StopTravel(string reason)
        {
            runner.AddLog($"City Chat Flooder stopped: {reason}. No further announcements will be sent.");
            runner.Cancel();
        }

        bool IsAt(string world, string? city = null)
        {
            if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != runContentId)
                return false;
            var currentWorld = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString();
            if (!string.Equals(currentWorld, world, StringComparison.OrdinalIgnoreCase))
                return false;
            if (city == null) return true;
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            return sheet.TryGetRow(Plugin.ClientState.TerritoryType, out var row)
                && row.PlaceName.Value.Name.ToString().Equals(city, StringComparison.OrdinalIgnoreCase);
        }

        bool IsReadyAt(string world, string? city = null)
            => IsAt(world, city) && plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy
                && CharacterSafetyHelper.IsCharacterSafeWaitReady();

        void AddSafeArrivalWait(string world, string? city = null)
        {
            var safePasses = 0;
            var nextPassUtc = DateTime.MinValue;
            steps.Add(new TaskStep
            {
                Name = $"Verify safe arrival: {world} / {city ?? "world"}",
                IsComplete = () =>
                {
                    if (!IsReadyAt(world, city))
                    {
                        safePasses = 0;
                        nextPassUtc = DateTime.MinValue;
                        return false;
                    }
                    if (DateTime.UtcNow < nextPassUtc) return false;
                    safePasses++;
                    nextPassUtc = DateTime.UtcNow.AddSeconds(1);
                    if (safePasses < 3) return false;
                    runner.SuppressLogoutCancel = false;
                    return safePasses >= 3;
                },
                TimeoutSec = 90f,
                OnTimeout = () => StopTravel($"could not verify three safe checks at {world} / {city ?? "world"}"),
            });
        }

        void AddTravel(string world, string? city = null)
        {
            var skipped = false;
            var sawBusy = false;
            var crossDc = false;
            var completionTimeout = 120f;
            var target = city ?? world;
            var completionDeadlineUtc = DateTime.MaxValue;
            steps.Add(new TaskStep
            {
                Name = $"Travel to: {target}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"{world} / {city ?? "world travel"}";
                    if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != runContentId)
                    {
                        StopTravel("the original character is unavailable before travel");
                        return;
                    }
                    if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy)
                    {
                        StopTravel("Lifestream is unavailable or already busy before travel");
                        return;
                    }
                    skipped = IsAt(world, city);
                    if (skipped)
                    {
                        runner.AddLog($"Already at {target} on {world}; verifying arrival.");
                        return;
                    }
                    var currentWorld = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString() ?? "";
                    var sourceInfo = WorldData.GetByName(currentWorld);
                    var targetInfo = WorldData.GetByName(world);
                    if (city == null && (sourceInfo == null || targetInfo == null))
                    {
                        StopTravel("cannot determine the source or destination data center");
                        return;
                    }
                    crossDc = city == null && sourceInfo!.DataCenter != targetInfo!.DataCenter;
                    completionTimeout = crossDc ? 600f : 120f;
                    // Arm before /li: DC travel can log out before the next task tick.
                    runner.SuppressLogoutCancel = crossDc;
                    runner.AddLog($"Traveling to {target} on {world}; waiting up to {completionTimeout:0}s for completion.");
                    ChatHelper.SendMessage($"/li {target}");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Observe travel start: {target}",
                ShouldSkip = () => skipped,
                IsComplete = () =>
                {
                    if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy)) return false;
                    if (busy)
                    {
                        sawBusy = true;
                        runner.AddLog($"Lifestream travel started: {target}.");
                        return true;
                    }
                    // Fast travel may finish between ticks; require the exact safe destination.
                    return IsReadyAt(world, city);
                },
                TimeoutSec = 45f,
                OnTimeout = () => StopTravel($"Lifestream did not start travel to {target} within 45s"),
            });
            steps.Add(new TaskStep
            {
                Name = $"Wait for travel completion: {target}",
                ShouldSkip = () => skipped,
                OnEnter = () => completionDeadlineUtc = DateTime.UtcNow.AddSeconds(completionTimeout),
                IsComplete = () =>
                {
                    if (plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy
                        && (sawBusy || IsReadyAt(world, city)) && IsAt(world, city)) return true;
                    if (DateTime.UtcNow >= completionDeadlineUtc)
                        StopTravel($"travel to {target} did not finish at the intended destination within {completionTimeout:0}s");
                    return false;
                },
                TimeoutSec = 600f,
                OnTimeout = () => StopTravel($"travel to {target} did not finish at the intended destination within {completionTimeout:0}s"),
            });
            AddSafeArrivalWait(world, city);
        }

        // -- Pre-flight: CharacterSafeWait --
        foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Key("S80D1F3CAE51EBF31"), 30f))
        {
            steps.Add(new TaskStep
            {
                NameMessage = sw.NameMessage,
                OnEnter = sw.OnEnter,
                IsComplete = sw.IsComplete,
                TimeoutSec = sw.TimeoutSec,
                OnTimeout = () => StopTravel("initial character safety wait timed out"),
            });
        }

        var visitIndex = 0;
        foreach (var world in orderedWorlds)
        {
            var capturedWorld = world;

            AddTravel(capturedWorld);

            foreach (var city in cityList)
            {
                var capturedCity = city;
                var capturedIdx = visitIndex++;

                AddTravel(capturedWorld, capturedCity);

                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S1B4E6B2C881F9833", capturedCity), cfg.FloorderWaitBetweenCities));

                AddSafeArrivalWait(capturedWorld, capturedCity);

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S6FAE5AEBB16DE272", capturedCity),
                    OnEnter = () =>
                    {
                        if (!IsReadyAt(capturedWorld, capturedCity))
                        {
                            StopTravel("character, destination or Lifestream readiness changed before announcement");
                            return;
                        }
                        var announcements = cfg.FloorderAnnouncements;
                        if (announcements.Count == 0) return;
                        var msg = announcements[floorderRng.Next(announcements.Count)];
                        ChatHelper.SendMessage($"{cfg.FloorderChatChannel} {msg}");
                        runner.AddLog($"  [{capturedCity}] {cfg.FloorderChatChannel}: {msg}");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });

                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S2E931F567D165739", capturedCity), cfg.FloorderWaitAfterAnnounce));

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S609E533F9CC93FC6", capturedWorld, capturedCity),
                    OnEnter = () =>
                    {
                        runner.CompletedItems = capturedIdx + 1;
                        runner.AddLog($"  Done: {capturedCity} on {capturedWorld} ({capturedIdx + 1}/{totalCityVisits})");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 1f,
                });
            }
        }

        if (cfg.FloorderEnableLooping)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S712566DCDD8182F0"),
                OnEnter = () => runner.AddLog($"==== Cycle complete. Waiting {cfg.FloorderLoopDelayMinutes:F0} min before next loop... ===="),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SF682F58F565C8420"), cfg.FloorderLoopDelayMinutes * 60f));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SA25ECB66C3F66798"),
                OnEnter = () =>
                {
                    runner.AddLog("==== Restarting flooding cycle... ====");
                    Plugin.ScheduleOnGameThread(() => StartCityChatFlooder(), delay: TimeSpan.FromMilliseconds(500));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }
        else
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SDB62D47B5AC8EB14"),
                OnEnter = () => runner.AddLog($"==== Completed! {totalCityVisits} city visits across {orderedWorlds.Count} worlds ===="),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S5C5514B66531FC2E"), steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: totalCityVisits, suppressLogoutCancel: false, haltOnStepError: true))
            return;

        AutoOpenTaskLogIfVerbose(ref floorderShowLog);
    }
}
