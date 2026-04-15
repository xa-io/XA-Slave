using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // ── All known built-in menu labels for custom fav target picker ──
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

    private const int MaxCustomFavItems = 4;
    private const int MaxResolutionFavItems = 4;

    // custom fav slot state (parallel arrays, index-stable)
    private bool[] pluginOpsFavCustomEnabled = new bool[MaxCustomFavItems];
    private string[] pluginOpsFavCustomMenuInputs = new string[MaxCustomFavItems];

    // resolution slot state
    private bool[] pluginOpsFavResEnabled = new bool[MaxResolutionFavItems];
    private string[] pluginOpsFavResWidthInputs = new string[MaxResolutionFavItems];
    private string[] pluginOpsFavResHeightInputs = new string[MaxResolutionFavItems];

    private bool pluginOpsFavInputsInitialized;

    private void EnsurePluginOpsFavInputsInitialized()
    {
        if (pluginOpsFavInputsInitialized) return;
        pluginOpsFavInputsInitialized = true;

        var cfg = plugin.Configuration;

        for (var i = 0; i < MaxCustomFavItems; i++)
        {
            var item = i < cfg.TitleBarFavCustomItems.Count ? cfg.TitleBarFavCustomItems[i] : null;
            pluginOpsFavCustomEnabled[i] = item?.Enabled ?? false;
            pluginOpsFavCustomMenuInputs[i] = item?.MenuTarget ?? string.Empty;
        }

        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            var item = i < cfg.TitleBarFavResolutionItems.Count ? cfg.TitleBarFavResolutionItems[i] : null;
            pluginOpsFavResEnabled[i] = item?.Enabled ?? false;
            pluginOpsFavResWidthInputs[i] = item != null ? item.Width.ToString() : "500";
            pluginOpsFavResHeightInputs[i] = item != null ? item.Height.ToString() : "345";
        }
    }

    private static void FavRowLabel(string text)
    {
        ImGui.TextUnformatted(text);
    }

    private void DrawPluginOperationsTask()
    {
        EnsurePluginOpsFavInputsInitialized();

        var cfg = plugin.Configuration;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Plugin Operations");
        ImGui.TextDisabled("Configure XA Slave startup window behavior.");
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

        var verboseTaskLogging = cfg.VerboseTaskLogging;
        if (ImGui.Checkbox("Verbose Task Logging", ref verboseTaskLogging))
        {
            cfg.VerboseTaskLogging = verboseTaskLogging;
            cfg.Save();
        }

        ImGui.TextDisabled("Off: normal user-facing task logs. On: detailed step timing, relog wait state, and CharacterSafeWait diagnostics.");

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
        ImGui.TextDisabled("Fixed Actions");
        ImGui.Spacing();

        // Kill Game
        var killGame = cfg.TitleBarFavKillGameEnabled;
        if (ImGui.Checkbox("##favKillGame", ref killGame))
        {
            cfg.TitleBarFavKillGameEnabled = killGame;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel("Kill Game  [skull]  — hold Ctrl+Shift to fire");

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
            ImGui.SetNextItemWidth(200f);
            if (ImGui.BeginCombo("##favModListName", string.IsNullOrEmpty(currentName) ? "(pick list)" : currentName))
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
                ImGui.EndCombo();
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
        ImGui.TextDisabled("Custom Favourites  (up to 4)");
        ImGui.TextDisabled("  Check to show button. Icon: slot 1=bookmark, 2=hashtag, 3=thumbsup, 4=star.");
        ImGui.Spacing();

        var changed = false;

        for (var i = 0; i < MaxCustomFavItems; i++)
        {
            ImGui.PushID($"favCustom{i}");

            var en = pluginOpsFavCustomEnabled[i];
            if (ImGui.Checkbox($"##en{i}", ref en))
            {
                pluginOpsFavCustomEnabled[i] = en;
                changed = true;
            }
            ImGui.SameLine();

            ImGui.SetNextItemWidth(220f);
            var comboLabel = pluginOpsFavCustomMenuInputs[i].Length > 0
                ? pluginOpsFavCustomMenuInputs[i]
                : "(none — click to pick)";
            if (ImGui.BeginCombo($"##menu{i}", comboLabel))
            {
                if (ImGui.Selectable("(none)", string.IsNullOrEmpty(pluginOpsFavCustomMenuInputs[i])))
                {
                    pluginOpsFavCustomMenuInputs[i] = string.Empty;
                    changed = true;
                }

                foreach (var menuLabel in AllBuiltInMenuLabels)
                {
                    var selected = pluginOpsFavCustomMenuInputs[i] == menuLabel;
                    if (ImGui.Selectable(menuLabel, selected))
                    {
                        pluginOpsFavCustomMenuInputs[i] = menuLabel;
                        changed = true;
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            FavRowLabel($"[{CustomFavSlotLabels[i]}]");

            ImGui.PopID();
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
            ImGui.PushID($"favRes{i}");

            var en = pluginOpsFavResEnabled[i];
            if (ImGui.Checkbox($"##en{i}", ref en))
            {
                pluginOpsFavResEnabled[i] = en;
                changed = true;
            }
            ImGui.SameLine();

            ImGui.SetNextItemWidth(60f);
            if (ImGui.InputText($"##resW{i}", ref pluginOpsFavResWidthInputs[i], 6))
                changed = true;

            ImGui.SameLine();
            ImGui.TextDisabled("x");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            if (ImGui.InputText($"##resH{i}", ref pluginOpsFavResHeightInputs[i], 6))
                changed = true;

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

            ImGui.PopID();
        }

        if (changed)
            SaveResolutionFavItems(cfg);
    }

    private void SaveCustomFavItems(Configuration cfg)
    {
        cfg.TitleBarFavCustomItems.Clear();
        for (var i = 0; i < MaxCustomFavItems; i++)
        {
            cfg.TitleBarFavCustomItems.Add(new TitleBarFavCustomItem
            {
                Enabled = pluginOpsFavCustomEnabled[i],
                MenuTarget = pluginOpsFavCustomMenuInputs[i],
            });
        }

        cfg.Save();
        RebuildTitleBarFavButtons();
    }

    private void SaveResolutionFavItems(Configuration cfg)
    {
        cfg.TitleBarFavResolutionItems.Clear();
        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            if (!int.TryParse(pluginOpsFavResWidthInputs[i], out var w) || w <= 0)
                w = 500;
            if (!int.TryParse(pluginOpsFavResHeightInputs[i], out var h) || h <= 0)
                h = 345;

            cfg.TitleBarFavResolutionItems.Add(new TitleBarFavResolutionItem
            {
                Enabled = pluginOpsFavResEnabled[i],
                Width = w,
                Height = h,
            });
        }

        cfg.Save();
        RebuildTitleBarFavButtons();
    }
}
