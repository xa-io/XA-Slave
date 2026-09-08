using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // -- All known built-in menu labels for custom fav target picker --
    private static readonly string[] AllBuiltInMenuLabels =
    {
        "AutoRetainer Helper",
        "Save to XA Database",
        "Auto-Glam Weather",
        "City Chat Flooder",
        "Xagman",
        "Monthly Relogger",
        "Prep Logistics",
        "Auto-Accept FC Invites",
        "FC Permissions Updater",
        "Check Duplicate Plots",
        "Return Alts To Homeworlds",
        "Refresh Sub/Bell/Chest",
        "Eureka Instance Hunter",
        "XA Mods",
        "Window Renamer",
        "Plugin Operations",
        "Export Data",
        "Repo List",
        "IPC Calls Available",
        "Commands",
        "Splash Screen",
    };

    private static readonly string[] CustomFavSlotLabels = { "Bookmark", "Hashtag", "ThumbsUp", "Star" };
    private const int MaxResolutionFavItems = 4;

    // custom fav slot state (parallel lists, index-stable)
    private readonly List<bool> pluginOpsFavCustomEnabled = new();
    private readonly List<string> pluginOpsFavCustomMenuInputs = new();
    private readonly List<string> pluginOpsFavCustomSearchInputs = new();

    // resolution slot state
    private bool[] pluginOpsFavResEnabled = new bool[MaxResolutionFavItems];
    private string[] pluginOpsFavResWidthInputs = new string[MaxResolutionFavItems];
    private string[] pluginOpsFavResHeightInputs = new string[MaxResolutionFavItems];

    private bool pluginOpsFavInputsInitialized;
    private int pluginOpsFavConfigFingerprint;

    private void EnsurePluginOpsFavInputsInitialized()
    {
        var cfg = plugin.Configuration;
        var fingerprint = GetPluginOpsFavConfigFingerprint(cfg);
        if (pluginOpsFavInputsInitialized && fingerprint == pluginOpsFavConfigFingerprint)
            return;
        if (pluginOpsFavInputsInitialized && ImGui.IsAnyItemActive())
            return;

        pluginOpsFavInputsInitialized = true;
        pluginOpsFavConfigFingerprint = fingerprint;

        pluginOpsFavCustomEnabled.Clear();
        pluginOpsFavCustomMenuInputs.Clear();
        pluginOpsFavCustomSearchInputs.Clear();
        foreach (var item in cfg.TitleBarFavCustomItems)
        {
            pluginOpsFavCustomEnabled.Add(item.Enabled);
            pluginOpsFavCustomMenuInputs.Add(TitleBarFavSelectionKeys.Normalize(item.SelectionKey, item.MenuTarget));
            pluginOpsFavCustomSearchInputs.Add(string.Empty);
        }

        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            var item = i < cfg.TitleBarFavResolutionItems.Count ? cfg.TitleBarFavResolutionItems[i] : null;
            pluginOpsFavResEnabled[i] = item?.Enabled ?? false;
            pluginOpsFavResWidthInputs[i] = item != null ? item.Width.ToString() : "500";
            pluginOpsFavResHeightInputs[i] = item != null ? item.Height.ToString() : "345";
        }
    }

    private static int GetPluginOpsFavConfigFingerprint(Configuration cfg)
    {
        var hash = new HashCode();
        foreach (var item in cfg.TitleBarFavCustomItems)
        {
            hash.Add(item.Enabled);
            hash.Add(item.SelectionKey, StringComparer.Ordinal);
            hash.Add(item.MenuTarget, StringComparer.Ordinal);
        }
        foreach (var item in cfg.TitleBarFavResolutionItems)
        {
            hash.Add(item.Enabled);
            hash.Add(item.Width);
            hash.Add(item.Height);
        }
        return hash.ToHashCode();
    }

    private static void FavRowLabel(string text)
    {
        ImGui.TextUnformatted(text);
    }

    private static string GetCustomFavSlotLabel(int slotIndex)
    {
        return slotIndex < CustomFavSlotLabels.Length
            ? CustomFavSlotLabels[slotIndex]
            : $"Star {slotIndex + 1}";
    }

    private void AddPluginOpsCustomFavRow()
    {
        pluginOpsFavCustomEnabled.Add(false);
        pluginOpsFavCustomMenuInputs.Add(string.Empty);
        pluginOpsFavCustomSearchInputs.Add(string.Empty);
    }

    private void RemovePluginOpsCustomFavRowAt(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= pluginOpsFavCustomEnabled.Count)
            return;

        pluginOpsFavCustomEnabled.RemoveAt(slotIndex);
        pluginOpsFavCustomMenuInputs.RemoveAt(slotIndex);
        pluginOpsFavCustomSearchInputs.RemoveAt(slotIndex);
    }

    private static bool MatchesTitleBarFavSelectionSearch(TitleBarFavSelectionOption option, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        foreach (var term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (option.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || option.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                || option.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private void DrawPluginOperationsTask()
    {
        EnsurePluginOpsFavInputsInitialized();

        var cfg = plugin.Configuration;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Plugin Operations");
        ImGui.TextDisabled("Configure XA Slave startup, logging, and window behavior.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var openPluginOnLoad = cfg.OpenPluginOnLoad;
        if (ImGui.Checkbox("Open Plugin on Load", ref openPluginOnLoad))
        {
            cfg.OpenPluginOnLoad = openPluginOnLoad;
            cfg.Save();
        }

        ImGui.TextDisabled("Opens XA Slave when the plugin loads and when the character logs in.");

        ImGui.Spacing();

        var customResolutionOnLoad = cfg.CustomResolutionOnLoadEnabled;
        if (ImGui.Checkbox("Custom Resolution on Plugin Load", ref customResolutionOnLoad))
        {
            cfg.CustomResolutionOnLoadEnabled = customResolutionOnLoad;
            cfg.Save();
        }

        ImGui.TextDisabled("Force-resizes the game window to a custom resolution when the plugin loads (same engine as XA Mods → Custom Resolutions).");

        using (ImRaii.Disabled(!cfg.CustomResolutionOnLoadEnabled))
        {
            ImGui.Indent();

            var customResWidth = cfg.CustomResolutionOnLoadWidth;
            ImGui.SetNextItemWidth(Scale(160f));
            if (ImGui.InputInt("Width##CustomResolutionOnLoadWidth", ref customResWidth))
            {
                cfg.CustomResolutionOnLoadWidth = Math.Clamp(customResWidth, 250, 16384);
                cfg.Save();
            }

            var customResHeight = cfg.CustomResolutionOnLoadHeight;
            ImGui.SetNextItemWidth(Scale(160f));
            if (ImGui.InputInt("Height##CustomResolutionOnLoadHeight", ref customResHeight))
            {
                cfg.CustomResolutionOnLoadHeight = Math.Clamp(customResHeight, 200, 16384);
                cfg.Save();
            }

            var customResIgnoreMin = cfg.CustomResolutionOnLoadIgnoreMinimumWindowSize;
            if (ImGui.Checkbox("Ignore Minimum Window Size##CustomResolutionOnLoad", ref customResIgnoreMin))
            {
                cfg.CustomResolutionOnLoadIgnoreMinimumWindowSize = customResIgnoreMin;
                cfg.Save();
            }

            ImGui.TextDisabled("Lowers the client minimum window size so resolutions below 1024x720 stick instead of snapping back. Recommended on for small resolutions.");

            ImGui.Unindent();
        }

        var showVersionInTitle = cfg.ShowVersionInUpdatesTitle;
        if (ImGui.Checkbox("Show Version in Window Title", ref showVersionInTitle))
        {
            cfg.ShowVersionInUpdatesTitle = showVersionInTitle;
            cfg.ShowVersionInWindowTitleDefaultApplied = true;
            cfg.Save();
        }

        ImGui.TextDisabled("Appends the current version number to the main XA Slave window title bar. The Updates window stays XA Slave - Updates.");

        ImGui.Spacing();

        var verboseTaskLogging = cfg.VerboseTaskLogging;
        if (ImGui.Checkbox("Verbose Task Logging", ref verboseTaskLogging))
        {
            cfg.VerboseTaskLogging = verboseTaskLogging;
            cfg.Save();
        }

        ImGui.TextDisabled("Off: normal user-facing task logs. On: detailed step timing, relog wait state, and CharacterSafeWait diagnostics.");

        ImGui.Spacing();

        var messageLogEnabled = cfg.MessageLogEnabled;
        if (ImGui.Checkbox("Log Chat, Messages and Emotes to /xllog", ref messageLogEnabled))
        {
            cfg.MessageLogEnabled = messageLogEnabled;
            cfg.Save();
        }

        ImGui.TextWrapped("Writes delivered chat, system/error messages, and emotes to the Dalamud log, including sender names and private chat text.");
        ImGui.TextDisabled("Off by default. Applies immediately and saves across reloads. Xagman error detection stays active.");

        ImGui.Spacing();

        if (ImGui.Button("⬆  Show Updates"))
        {
            Plugin.Instance.UpdatesWindow.Toggle();
        }
        ImGui.TextDisabled("Open the version history window.  Also available via /xa updates.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Titlebar Favourite Buttons");
        ImGui.TextDisabled("Add quick-action buttons to the right of the title bar, to the left of collapse/close.");
        ImGui.Spacing();

        DrawPluginOpsFavFixedButtons(cfg);
        ImGui.Spacing();
        DrawPluginOpsFavCustomItems(cfg);
        ImGui.Spacing();
        DrawPluginOpsFavResolutionItems(cfg);
    }

    private void DrawPluginOpsFavFixedButtons(Configuration cfg)
    {
        EnsureKillGameTitleBarDependency();

        ImGui.TextDisabled("Fixed Actions");
        ImGui.Spacing();

        // Kill Game
        var killGame = cfg.TitleBarFavKillGameEnabled;
        if (ImGui.Checkbox("##favKillGame", ref killGame))
        {
            if (killGame && !cfg.InstantLogoutEnabled)
            {
                var instantLogoutApplied = plugin.SetXAModEnabledByKey("instant-logout", true, out _);
                cfg.TitleBarFavKillGameEnabled = instantLogoutApplied;
            }
            else
            {
                cfg.TitleBarFavKillGameEnabled = killGame;
            }

            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("Kill Game  [skull]  - hold Ctrl+Shift to fire");

        ImGui.TextDisabled("Enabling this also turns on XA Mods > Instant Logout. Disabling this titlebar icon does not disable the XA Mod.");
        ImGui.Spacing();

        // Disable All XA Mods
        var disableMods = cfg.TitleBarFavDisableAllModsEnabled;
        if (ImGui.Checkbox("##favDisableMods", ref disableMods))
        {
            cfg.TitleBarFavDisableAllModsEnabled = disableMods;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("Disable All XA Mods  [recycle]");

        ImGui.Spacing();

        // Fav Mod List
        var modList = cfg.TitleBarFavModListEnabled;
        if (ImGui.Checkbox("##favModList", ref modList))
        {
            cfg.TitleBarFavModListEnabled = modList;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("Load XA Mod List  [list]");

        if (modList)
        {
            ImGui.SameLine();
            var savedNames = plugin.GetSavedModListNames();
            var currentName = cfg.TitleBarFavModListName;
            ImGui.SetNextItemWidth(Scale(200f));
            using (var imguiScope317 = ImRaii.Combo("##favModListName", string.IsNullOrEmpty(currentName) ? "(pick list)" : currentName))
            if (imguiScope317)
            {
                foreach (var name in savedNames)
                {
                    var sel = currentName == name;
                    if (ImGui.Selectable(name, sel))
                    {
                        cfg.TitleBarFavModListName = name;
                        cfg.Save();
                        RebuildTitleBarFavButtons();
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }

            }
        }

        ImGui.Spacing();

        // Auto-Glam Weather
        var glamWeather = cfg.TitleBarFavGlamWeatherEnabled;
        if (ImGui.Checkbox("##favGlamWeather", ref glamWeather))
        {
            cfg.TitleBarFavGlamWeatherEnabled = glamWeather;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("Auto-Glam Weather toggle  [sun]");

        ImGui.Spacing();

        // AR Pre-Process
        var arPre = cfg.TitleBarFavArPreProcessEnabled;
        if (ImGui.Checkbox("##favArPre", ref arPre))
        {
            cfg.TitleBarFavArPreProcessEnabled = arPre;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("AR Pre-Process toggle  [gas pump]");

        ImGui.Spacing();

        // AR Post-Process
        var arPost = cfg.TitleBarFavArPostProcessEnabled;
        if (ImGui.Checkbox("##favArPost", ref arPost))
        {
            cfg.TitleBarFavArPostProcessEnabled = arPost;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("AR Post-Process toggle  [flag]");
    }

    private void DrawPluginOpsFavCustomItems(Configuration cfg)
    {
        ImGui.TextDisabled("Custom Favourites");
        ImGui.TextDisabled("  Add or remove rows as needed. The first four rows use bookmark, hashtag, thumbsup, and star; extra rows use star.");
        ImGui.TextDisabled("  Rows can open panels, toggle any XA Mod, drive Special Rendering Modes tools, fire Sit / Doze, run All XA Mods Off, or trigger Stop All Automated Tasks.");
        ImGui.TextDisabled("  Use the search box inside the picker to filter large panel and XA Mod lists.");
        ImGui.Spacing();

        var changed = false;
        var removedIndex = -1;
        var selectionOptions = GetTitleBarFavSelectionOptions();

        if (ImGui.Button("Add Favourite"))
        {
            AddPluginOpsCustomFavRow();
            SaveCustomFavItems(cfg);
        }

        if (pluginOpsFavCustomEnabled.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("No custom favourites configured.");
        }

        ImGui.Spacing();

        for (var i = 0; i < pluginOpsFavCustomEnabled.Count; i++)
        {
            using (ImRaii.PushId($"favCustom{i}"))
            {

                var en = pluginOpsFavCustomEnabled[i];
                if (ImGui.Checkbox($"##en{i}", ref en))
                {
                    pluginOpsFavCustomEnabled[i] = en;
                    changed = true;
                }
                ImGui.SameLine();

                ImGui.SetNextItemWidth(Scale(220f));
                var comboLabel = pluginOpsFavCustomMenuInputs[i].Length > 0
                    ? GetTitleBarFavSelectionLabel(pluginOpsFavCustomMenuInputs[i])
                    : "(none - click to pick)";
                using (var imguiScope416 = ImRaii.Combo($"##menu{i}", comboLabel))
                if (imguiScope416)
                {
                    var searchInput = pluginOpsFavCustomSearchInputs[i];
                    if (ImGui.IsWindowAppearing())
                    {
                        searchInput = string.Empty;
                        ImGui.SetKeyboardFocusHere();
                    }

                    ImGui.SetNextItemWidth(-1f);
                    ImGui.InputTextWithHint($"##menuSearch{i}", "Search panels, mods, actions", ref searchInput, 128);
                    pluginOpsFavCustomSearchInputs[i] = searchInput;

                    ImGui.Separator();

                    if (ImGui.Selectable("(none)", string.IsNullOrEmpty(pluginOpsFavCustomMenuInputs[i])))
                    {
                        pluginOpsFavCustomMenuInputs[i] = string.Empty;
                        pluginOpsFavCustomSearchInputs[i] = string.Empty;
                        changed = true;
                    }

                    var lastCategory = string.Empty;
                    var matchesFound = false;
                    foreach (var option in selectionOptions)
                    {
                        if (!MatchesTitleBarFavSelectionSearch(option, searchInput))
                            continue;

                        matchesFound = true;
                        if (!option.Category.Equals(lastCategory, StringComparison.Ordinal))
                        {
                            if (lastCategory.Length > 0)
                                ImGui.Spacing();

                            ImGui.TextDisabled(option.Category);
                            lastCategory = option.Category;
                        }

                        var selected = pluginOpsFavCustomMenuInputs[i].Equals(option.Key, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(option.Label, selected))
                        {
                            pluginOpsFavCustomMenuInputs[i] = option.Key;
                            pluginOpsFavCustomSearchInputs[i] = string.Empty;
                            changed = true;
                        }
                        if (selected) ImGui.SetItemDefaultFocus();
                    }

                    if (!matchesFound)
                        ImGui.TextDisabled("No matches.");


                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"x##remove{i}"))
                {
                    removedIndex = i;
                }

                ImGui.SameLine();
                FavRowLabel($"[{GetCustomFavSlotLabel(i)}]");

            }
        }

        if (removedIndex >= 0)
        {
            RemovePluginOpsCustomFavRowAt(removedIndex);
            SaveCustomFavItems(cfg);
            return;
        }

        if (changed)
            SaveCustomFavItems(cfg);
    }

    private void DrawPluginOpsFavResolutionItems(Configuration cfg)
    {
        ImGui.TextDisabled("Resolution Shortcuts  (up to 4)");
        ImGui.TextDisabled("  Check to show button. Enter width x height. [x] removes the slot.");
        ImGui.Spacing();

        var changed = false;

        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            using (ImRaii.PushId($"favRes{i}"))
            {

                var en = pluginOpsFavResEnabled[i];
                if (ImGui.Checkbox($"##en{i}", ref en))
                {
                    pluginOpsFavResEnabled[i] = en;
                    changed = true;
                }
                ImGui.SameLine();

                ImGui.SetNextItemWidth(Scale(60f));
                ImGui.InputText($"##resW{i}", ref pluginOpsFavResWidthInputs[i], 6);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    pluginOpsFavResWidthInputs[i] = NormalizeResolutionInput(pluginOpsFavResWidthInputs[i], 250, 500);
                    changed = true;
                }

                ImGui.SameLine();
                ImGui.TextDisabled("x");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Scale(60f));
                ImGui.InputText($"##resH{i}", ref pluginOpsFavResHeightInputs[i], 6);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    pluginOpsFavResHeightInputs[i] = NormalizeResolutionInput(pluginOpsFavResHeightInputs[i], 200, 345);
                    changed = true;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"x##clr{i}"))
                {
                    pluginOpsFavResEnabled[i] = false;
                    pluginOpsFavResWidthInputs[i] = "500";
                    pluginOpsFavResHeightInputs[i] = "345";
                    changed = true;
                }

                ImGui.SameLine();
                FavRowLabel($"[desktop]  {pluginOpsFavResWidthInputs[i]}x{pluginOpsFavResHeightInputs[i]}");

            }
        }

        if (changed)
            SaveResolutionFavItems(cfg);
    }

    private void SaveCustomFavItems(Configuration cfg)
    {
        cfg.TitleBarFavCustomItems.Clear();
        for (var i = 0; i < pluginOpsFavCustomEnabled.Count; i++)
        {
            cfg.TitleBarFavCustomItems.Add(new TitleBarFavCustomItem
            {
                Enabled = pluginOpsFavCustomEnabled[i],
                SelectionKey = pluginOpsFavCustomMenuInputs[i],
                MenuTarget = TitleBarFavSelectionKeys.TryGetMenuTarget(pluginOpsFavCustomMenuInputs[i], out var menuTarget)
                    ? menuTarget
                    : string.Empty,
            });
        }

        pluginOpsFavConfigFingerprint = GetPluginOpsFavConfigFingerprint(cfg);
        cfg.SaveDeferred();
        RebuildTitleBarFavButtons();
    }

    private void SaveResolutionFavItems(Configuration cfg)
    {
        cfg.TitleBarFavResolutionItems.Clear();
        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            pluginOpsFavResWidthInputs[i] = NormalizeResolutionInput(pluginOpsFavResWidthInputs[i], 250, 500);
            pluginOpsFavResHeightInputs[i] = NormalizeResolutionInput(pluginOpsFavResHeightInputs[i], 200, 345);
            var w = int.Parse(pluginOpsFavResWidthInputs[i]);
            var h = int.Parse(pluginOpsFavResHeightInputs[i]);

            cfg.TitleBarFavResolutionItems.Add(new TitleBarFavResolutionItem
            {
                Enabled = pluginOpsFavResEnabled[i],
                Width = w,
                Height = h,
            });
        }

        pluginOpsFavConfigFingerprint = GetPluginOpsFavConfigFingerprint(cfg);
        cfg.SaveDeferred();
        RebuildTitleBarFavButtons();
    }

    private static string NormalizeResolutionInput(string value, int minimum, int fallback)
    {
        if (!int.TryParse(value, out var parsed))
            parsed = fallback;
        return Math.Clamp(parsed, minimum, 16384).ToString();
    }
}
