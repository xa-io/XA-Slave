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

        // ── Title ──
        ImGui.TextColored(cyan, "City Chat Flooder");
        ImGui.TextDisabled("Travel through worlds and cities, sending announcements in chat.");
        ImGui.TextDisabled("Worlds are processed in order: Region > Data Center > World.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Plugin Status ──
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        ImGui.Text("Required: ");
        ImGui.SameLine();
        ImGui.TextColored(lsOk ? green : red, lsOk ? "[Lifestream]" : "[Lifestream \u2717]");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Run Controls ──
        if (runner.IsRunning && runner.CurrentTaskName == "City Chat Flooder")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(yellow, $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);
            ImGui.Spacing();
            if (ImGui.Button("Cancel"))
                runner.Cancel();
        }
        else
        {
            // Error message (always visible when set)
            if (!string.IsNullOrEmpty(floorderError) && DateTime.UtcNow < floorderErrorExpiry)
            {
                ImGui.TextColored(red, floorderError);
                ImGui.Spacing();
            }

            var worldCount = cfg.FloorderSelectedWorlds.Count;
            var cityCount = cfg.FloorderSelectedCities.Count;
            var canStart = lsOk && worldCount > 0 && cityCount > 0 && cfg.FloorderAnnouncements.Count > 0
                && !runner.IsRunning && Plugin.PlayerState.IsLoaded;

            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button("Start Flooding"))
                StartCityChatFlooder();
            if (!canStart) ImGui.EndDisabled();

            ImGui.SameLine();
            var looping = cfg.FloorderEnableLooping;
            if (ImGui.Checkbox("Enable Looping", ref looping))
            {
                cfg.FloorderEnableLooping = looping;
                cfg.Save();
            }
            if (looping)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                var loopDelay = cfg.FloorderLoopDelayMinutes;
                if (ImGui.InputFloat("min##loop", ref loopDelay, 1f, 5f, "%.0f"))
                {
                    if (loopDelay < 1f) loopDelay = 1f;
                    if (loopDelay > 60f) loopDelay = 60f;
                    cfg.FloorderLoopDelayMinutes = loopDelay;
                    cfg.Save();
                }
            }

            ImGui.TextDisabled($"{worldCount} world(s) x {cityCount} city/cities x {cfg.FloorderAnnouncements.Count} announcement(s)");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Chat Channel + Timing ──
        ImGui.TextColored(cyan, "Chat Channel");
        ImGui.Spacing();
        var channelIdx = Array.IndexOf(FloorderChannelOptions, cfg.FloorderChatChannel);
        if (channelIdx < 0) channelIdx = 0;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("##FloorderChannel", ref channelIdx, FloorderChannelOptions, FloorderChannelOptions.Length))
        {
            cfg.FloorderChatChannel = FloorderChannelOptions[channelIdx];
            cfg.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Channel used for announcements");

        var waitCity = cfg.FloorderWaitBetweenCities;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputFloat("Wait between cities (sec)##fl", ref waitCity, 0.5f, 1.0f, "%.1f"))
        {
            if (waitCity < 1f) waitCity = 1f;
            if (waitCity > 30f) waitCity = 30f;
            cfg.FloorderWaitBetweenCities = waitCity;
            cfg.Save();
        }

        var waitAnn = cfg.FloorderWaitAfterAnnounce;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputFloat("Wait after announce (sec)##fl", ref waitAnn, 0.5f, 1.0f, "%.1f"))
        {
            if (waitAnn < 0.5f) waitAnn = 0.5f;
            if (waitAnn > 10f) waitAnn = 10f;
            cfg.FloorderWaitAfterAnnounce = waitAnn;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── World Selection Grid ──
        ImGui.TextColored(cyan, $"Worlds ({cfg.FloorderSelectedWorlds.Count} selected)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Select All##flworlds"))
        {
            foreach (var w in WorldData.Worlds) cfg.FloorderSelectedWorlds.Add(w.Name);
            cfg.FloorderSelectedWorlds = cfg.FloorderSelectedWorlds.Distinct().ToList();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear All##flworlds"))
        {
            cfg.FloorderSelectedWorlds.Clear();
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
                if (ImGui.SmallButton($"All##fl{region}"))
                {
                    foreach (var w in regionWorlds)
                        if (!cfg.FloorderSelectedWorlds.Contains(w.Name))
                            cfg.FloorderSelectedWorlds.Add(w.Name);
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"None##fl{region}"))
                {
                    foreach (var w in regionWorlds)
                        cfg.FloorderSelectedWorlds.Remove(w.Name);
                }

                if (ImGui.BeginTable($"FlDCTable_{region}", dcs.Length, ImGuiTableFlags.None))
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
                            }
                        }
                    }
                    ImGui.EndTable();
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── City Selection ──
        ImGui.TextColored(cyan, $"Cities ({cfg.FloorderSelectedCities.Count} selected)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Select All##flcities"))
        {
            foreach (var c in FloorderDefaultCities)
                if (!cfg.FloorderSelectedCities.Contains(c))
                    cfg.FloorderSelectedCities.Add(c);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear All##flcities"))
        {
            cfg.FloorderSelectedCities.Clear();
        }
        ImGui.Spacing();

        var citiesOpen = !floorderExpandedSections.Contains("cities_closed"); // open by default
        if (ImGui.ArrowButton("##fltoggle_cities", citiesOpen ? ImGuiDir.Down : ImGuiDir.Right))
        {
            if (citiesOpen) floorderExpandedSections.Add("cities_closed");
            else floorderExpandedSections.Remove("cities_closed");
        }
        ImGui.SameLine();
        ImGui.Text("Main Cities");

        if (citiesOpen)
        {
            var cols = 3;
            if (ImGui.BeginTable("FlCityTable", cols, ImGuiTableFlags.None))
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
                        }
                    }
                }
                ImGui.EndTable();
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
            ImGui.Text("Custom Cities");

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
                    }
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, red);
                    if (ImGui.SmallButton($"X##flrmcust_{i}"))
                    {
                        cfg.FloorderSelectedCities.Remove(city);
                        cfg.FloorderCustomCities.RemoveAt(i);
                        cfg.Save();
                        ImGui.PopStyleColor();
                        break;
                    }
                    ImGui.PopStyleColor();
                }
            }
        }

        ImGui.SetNextItemWidth(200);
        var cityEnter = ImGui.InputTextWithHint("##FlAddCity", "Add custom city...", ref floorderAddCityInput, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add City##fl") || cityEnter) && !string.IsNullOrWhiteSpace(floorderAddCityInput))
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

        // ── Announcements ──
        ImGui.TextColored(cyan, $"Announcements ({cfg.FloorderAnnouncements.Count})");
        ImGui.TextDisabled("One random announcement is picked per city visit.");
        ImGui.TextDisabled($"Maximum {FloorderMaxMessageLength} characters per message.");
        ImGui.Spacing();
        for (int i = 0; i < cfg.FloorderAnnouncements.Count; i++)
        {
            var msg = cfg.FloorderAnnouncements[i];
            var display = msg.Length > 80 ? msg[..77] + "..." : msg;
            var color = msg.Length > FloorderMaxMessageLength ? red : new Vector4(1f, 1f, 1f, 1f);
            ImGui.TextColored(color, $"  {i + 1}. {display}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{msg}\n\n({msg.Length}/{FloorderMaxMessageLength} chars)");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, red);
            if (ImGui.SmallButton($"X##flrma{i}"))
            {
                cfg.FloorderAnnouncements.RemoveAt(i);
                cfg.Save();
                ImGui.PopStyleColor();
                break;
            }
            ImGui.PopStyleColor();
        }

        ImGui.SetNextItemWidth(-100);
        var annEnter = ImGui.InputTextWithHint("##FlAddAnn", "New announcement message...", ref floorderAddAnnouncementInput, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        var charCount = floorderAddAnnouncementInput.Length;
        var charColor = charCount > FloorderMaxMessageLength ? red : charCount > FloorderMaxMessageLength * 0.8f ? yellow : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
        ImGui.TextColored(charColor, $"{charCount}/{FloorderMaxMessageLength} characters");
        ImGui.SameLine();
        if ((ImGui.Button("Add##flann") || annEnter) && !string.IsNullOrWhiteSpace(floorderAddAnnouncementInput))
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
            ImGui.PushStyleColor(ImGuiCol.Text, red);
            if (ImGui.SmallButton("Clear All Messages##flclearann"))
            {
                cfg.FloorderAnnouncements.Clear();
                cfg.Save();
            }
            ImGui.PopStyleColor();
        }

        // ── Log ──
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

        // ── Region validation ──
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
                    var msg = $"Cannot travel to {unreachable.Count} world(s) from {myRegion}: {string.Join(", ", unreachable.Take(5))}";
                    if (unreachable.Count > 5) msg += $" (+{unreachable.Count - 5} more)";
                    msg += ". Clear unreachable worlds and try again.";
                    floorderError = msg;
                    floorderErrorExpiry = DateTime.UtcNow.AddSeconds(30);
                    runner.AddLog(msg);
                    Plugin.Log.Warning($"[XASlave] City Chat Flooder: {msg}");
                    floorderShowLog = true;
                    return;
                }
            }
        }
        catch { /* proceed if check fails */ }

        var steps = new List<TaskStep>();
        runner.TotalItems = totalCityVisits;
        runner.CompletedItems = 0;

        // ── Pre-flight: CharacterSafeWait ──
        foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass("Pre-flight SafeWait", 30f))
            steps.Add(sw);

        var visitIndex = 0;
        foreach (var world in orderedWorlds)
        {
            var capturedWorld = world;

            // World travel — skip if already on this world
            var worldSkipped = new bool[] { false };
            steps.Add(new TaskStep
            {
                Name = $"Travel to {capturedWorld}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"World: {capturedWorld}";
                    try
                    {
                        var local = Plugin.ObjectTable.LocalPlayer;
                        var currentWorld = local?.CurrentWorld.ValueNullable?.Name.ToString() ?? "";
                        if (currentWorld.Equals(capturedWorld, StringComparison.OrdinalIgnoreCase))
                        {
                            runner.AddLog($"==== Already on {capturedWorld} — skipping world travel ====");
                            worldSkipped[0] = true;
                            return;
                        }
                    }
                    catch { /* proceed with travel */ }

                    runner.AddLog($"==== Traveling to world: {capturedWorld} ====");
                    ChatHelper.SendMessage($"/li {capturedWorld}");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });

            steps.Add(MonthlyReloggerTask.MakeDelay($"LS Init ({capturedWorld})", 2.0f));

            steps.Add(new TaskStep
            {
                Name = $"Wait LS ({capturedWorld})",
                IsComplete = () =>
                {
                    if (worldSkipped[0]) return true;
                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return false; }
                },
                TimeoutSec = 120f,
            });

            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"SafeWait ({capturedWorld})", 30f))
                steps.Add(sw);

            foreach (var city in cityList)
            {
                var capturedCity = city;
                var capturedIdx = visitIndex++;

                // City travel — skip if already in this city
                var citySkipped = new bool[] { false };
                steps.Add(new TaskStep
                {
                    Name = $"Travel to {capturedCity} on {capturedWorld}",
                    OnEnter = () =>
                    {
                        runner.CurrentItemLabel = $"{capturedWorld} > {capturedCity}";
                        try
                        {
                            var territoryId = Plugin.ClientState.TerritoryType;
                            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
                            if (sheet.TryGetRow(territoryId, out var row))
                            {
                                var zoneName = row.PlaceName.Value.Name.ToString();
                                if (zoneName.Equals(capturedCity, StringComparison.OrdinalIgnoreCase))
                                {
                                    runner.AddLog($"  Already in {capturedCity} — skipping city travel");
                                    citySkipped[0] = true;
                                    return;
                                }
                            }
                        }
                        catch { /* proceed with travel */ }

                        runner.AddLog($"  Traveling to {capturedCity}...");
                        ChatHelper.SendMessage($"/li {capturedCity}");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });

                steps.Add(MonthlyReloggerTask.MakeDelay($"LS Init ({capturedCity})", 2.0f));

                steps.Add(new TaskStep
                {
                    Name = $"Wait LS ({capturedCity})",
                    IsComplete = () =>
                    {
                        if (citySkipped[0]) return true;
                        try { return !plugin.IpcClient.LifestreamIsBusy(); }
                        catch { return false; }
                    },
                    TimeoutSec = 120f,
                });

                foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"SafeWait ({capturedCity})", 30f))
                    steps.Add(sw);

                steps.Add(MonthlyReloggerTask.MakeDelay($"City settle ({capturedCity})", cfg.FloorderWaitBetweenCities));

                steps.Add(new TaskStep
                {
                    Name = $"Announce in {capturedCity}",
                    OnEnter = () =>
                    {
                        var announcements = cfg.FloorderAnnouncements;
                        if (announcements.Count == 0) return;
                        var msg = announcements[floorderRng.Next(announcements.Count)];
                        ChatHelper.SendMessage($"{cfg.FloorderChatChannel} {msg}");
                        runner.AddLog($"  [{capturedCity}] {cfg.FloorderChatChannel}: {msg}");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });

                steps.Add(MonthlyReloggerTask.MakeDelay($"Post-announce ({capturedCity})", cfg.FloorderWaitAfterAnnounce));

                steps.Add(new TaskStep
                {
                    Name = $"Complete: {capturedWorld}/{capturedCity}",
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
                Name = "Loop: Waiting for next cycle",
                OnEnter = () => runner.AddLog($"==== Cycle complete. Waiting {cfg.FloorderLoopDelayMinutes:F0} min before next loop... ===="),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay("Loop Delay", cfg.FloorderLoopDelayMinutes * 60f));
            steps.Add(new TaskStep
            {
                Name = "Loop: Restart",
                OnEnter = () =>
                {
                    runner.AddLog("==== Restarting flooding cycle... ====");
                    Plugin.Framework.RunOnTick(() => StartCityChatFlooder(), TimeSpan.FromMilliseconds(500));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }
        else
        {
            steps.Add(new TaskStep
            {
                Name = "Flooding Complete",
                OnEnter = () => runner.AddLog($"==== Completed! {totalCityVisits} city visits across {orderedWorlds.Count} worlds ===="),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        plugin.TaskRunner.Start("City Chat Flooder", steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });

        floorderShowLog = true;
    }
}
