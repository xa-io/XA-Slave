using System;
using XASlave.Localization;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // -- All known built-in menu labels for custom fav target picker --
    private static readonly string[] AllBuiltInMenuLabels =
    {
        UiLocale.Native("S83A8F587FFB9A4C2"),
        UiLocale.Native("S0AED21970EED8844"),
        UiLocale.Native("SEFBF04736214F41C"),
        UiLocale.Native("S5C5514B66531FC2E"),
        "Xagman",
        UiLocale.Native("S4D398C5A9609297E"),
        UiLocale.Native("S2AE13753A8D90982"),
        UiLocale.Native("S26AC3F3DFD044284"),
        UiLocale.Native("SA6711086459806CB"),
        UiLocale.Native("SA455BD4F4CF7E92B"),
        UiLocale.Native("S99446740287A7C89"),
        UiLocale.Native("S85CC8B46B386EB84"),
        UiLocale.Native("S5578C356136F70AA"),
        "XA Mods",
        UiLocale.Native("S05F3A5D5B4F5775C"),
        UiLocale.Native("S7DD7E3FD85B28A2A"),
        UiLocale.Native("S2ED227401538531A"),
        UiLocale.Native("S273D7A925BD20F8A"),
        UiLocale.Native("S03BD054B237A0FA2"),
        UiLocale.Native("SB269DC4E81A528A4"),
        UiLocale.Native("S86F4EE2A3639FB2B"),
    };

    private static readonly string[] CustomFavSlotLabels = { UiLocale.Native("S1BAC4007880BA129"), UiLocale.Native("SB4E1300F7AC5CAA7"), UiLocale.Native("S10482F3CF8E4FCA2"), UiLocale.Native("SE357D396871D1012") };
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
        ImGui.TextUnformatted(UiLocale.T(text));
    }

    private static string GetCustomFavSlotLabel(int slotIndex)
    {
        return slotIndex < CustomFavSlotLabels.Length
            ? UiLocale.T(CustomFavSlotLabels[slotIndex])
            : UiLocale.FormatKey("S0A99B656138C31E7", slotIndex + 1);
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
                || option.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || GetLocalizedFavoriteLabel(option).Contains(term, StringComparison.OrdinalIgnoreCase)
                || UiLocale.T(option.Category).Contains(term, StringComparison.OrdinalIgnoreCase))
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

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), UiLocale.Get("S7DD7E3FD85B28A2A"));
        ImGui.TextDisabled(UiLocale.Get("S549D7BEFF93005E4"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        UiLocale.DrawLanguageSelector("OperationsLanguage", cfg, showLabel: true);
        ImGui.Spacing();

        var openPluginOnLoad = cfg.OpenPluginOnLoad;
        if (UiNative.Checkbox(UiLocale.Native("S07D88BC00368A01D"), ref openPluginOnLoad))
        {
            cfg.OpenPluginOnLoad = openPluginOnLoad;
            cfg.Save();
        }

        ImGui.TextDisabled(UiLocale.Get("SCA69709CBD6BBC2D"));

        ImGui.Spacing();

        var customResolutionOnLoad = cfg.CustomResolutionOnLoadEnabled;
        if (UiNative.Checkbox(UiLocale.Native("S2DBBF28C1D7B07C1"), ref customResolutionOnLoad))
        {
            cfg.CustomResolutionOnLoadEnabled = customResolutionOnLoad;
            cfg.Save();
        }

        ImGui.TextDisabled(UiLocale.Get("S5798EA0539A8A3EB"));

        using (ImRaii.Disabled(!cfg.CustomResolutionOnLoadEnabled))
        {
            ImGui.Indent();

            var customResWidth = cfg.CustomResolutionOnLoadWidth;
            ImGui.SetNextItemWidth(Scale(160f));
            if (UiNative.InputInt(UiLocale.Native("S3019947B6DAF18B0") + "##CustomResolutionOnLoadWidth", ref customResWidth))
            {
                cfg.CustomResolutionOnLoadWidth = Math.Clamp(customResWidth, 250, 16384);
                cfg.Save();
            }

            var customResHeight = cfg.CustomResolutionOnLoadHeight;
            ImGui.SetNextItemWidth(Scale(160f));
            if (UiNative.InputInt(UiLocale.Native("SBFE2ECA401797325") + "##CustomResolutionOnLoadHeight", ref customResHeight))
            {
                cfg.CustomResolutionOnLoadHeight = Math.Clamp(customResHeight, 200, 16384);
                cfg.Save();
            }

            var customResIgnoreMin = cfg.CustomResolutionOnLoadIgnoreMinimumWindowSize;
            if (UiNative.Checkbox(UiLocale.Native("SD9CCEE8B9E9A452B") + "##CustomResolutionOnLoad", ref customResIgnoreMin))
            {
                cfg.CustomResolutionOnLoadIgnoreMinimumWindowSize = customResIgnoreMin;
                cfg.Save();
            }

            ImGui.TextDisabled(UiLocale.Get("SAD0D4FD81C156819"));

            ImGui.Unindent();
        }

        var showVersionInTitle = cfg.ShowVersionInUpdatesTitle;
        if (UiNative.Checkbox(UiLocale.Native("S820A3236DFFB92C2"), ref showVersionInTitle))
        {
            cfg.ShowVersionInUpdatesTitle = showVersionInTitle;
            cfg.ShowVersionInWindowTitleDefaultApplied = true;
            cfg.Save();
        }

        ImGui.TextDisabled(UiLocale.Get("S59178E761D419FF4"));

        ImGui.Spacing();

        var verboseTaskLogging = cfg.VerboseTaskLogging;
        if (UiNative.Checkbox(UiLocale.Native("SB26EFA6CFC770C3B"), ref verboseTaskLogging))
        {
            cfg.VerboseTaskLogging = verboseTaskLogging;
            cfg.Save();
        }

        ImGui.TextDisabled(UiLocale.Get("SE0424DB4984690C3"));

        ImGui.Spacing();

        var messageLogEnabled = cfg.MessageLogEnabled;
        if (UiNative.Checkbox(UiLocale.Native("S0AC3D95C9DB80023"), ref messageLogEnabled))
        {
            cfg.MessageLogEnabled = messageLogEnabled;
            cfg.Save();
        }

        ImGui.TextWrapped(UiLocale.Get("S6D81DEA8C71452E6"));
        ImGui.TextDisabled(UiLocale.Get("S82E5F102393AA845"));

        if (UiNative.TreeNode(UiLocale.Native("S1F47450C226114E6")))
        {
            cfg.MessageLogDisabledTypes ??= new();
            ImGui.TextWrapped(UiLocale.Get("S5FB2389A32BDC05B"));
            if (UiNative.Button(UiLocale.Native("S06F08E7C2F8A8C00") + "##MessageLog"))
            {
                cfg.MessageLogDisabledTypes.Clear();
                cfg.Save();
            }
            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("S6CB2279DBFE762E0") + "##MessageLog"))
            {
                foreach (var type in Enum.GetValues<Dalamud.Game.Text.XivChatType>())
                    cfg.MessageLogDisabledTypes.Add((ushort)type);
                cfg.Save();
            }
            if (ImGui.BeginTable("MessageLogCategories", 2))
            {
                var seen = new HashSet<ushort>();
                foreach (var type in Enum.GetValues<Dalamud.Game.Text.XivChatType>())
                {
                    var id = (ushort)type;
                    if (!seen.Add(id)) continue;
                    ImGui.TableNextColumn();
                    var enabled = !cfg.MessageLogDisabledTypes.Contains(id);
                    if (UiNative.Checkbox($"{type} ({id})##MessageLogType", ref enabled))
                    {
                        if (enabled) cfg.MessageLogDisabledTypes.Remove(id);
                        else cfg.MessageLogDisabledTypes.Add(id);
                        cfg.Save();
                    }
                }
                ImGui.EndTable();
            }
            ImGui.TreePop();
        }

        ImGui.Spacing();

        if (UiNative.Button(UiLocale.Native("S442F6AC84868237B")))
        {
            Plugin.Instance.UpdatesWindow.Toggle();
        }
        ImGui.TextDisabled(UiLocale.Get("S54F629842C983FEC"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), UiLocale.Get("SAB65E9B248037C0C"));
        ImGui.TextDisabled(UiLocale.Get("S8761982E4347A015"));
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

        ImGui.TextDisabled(UiLocale.Get("S0B96B9109FAB6CFF"));
        ImGui.Spacing();

        // Kill Game
        var killGame = cfg.TitleBarFavKillGameEnabled;
        if (UiNative.Checkbox("##favKillGame", ref killGame))
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
        FavRowLabel(UiLocale.Native("S50692C3DE4A6F725"));

        ImGui.TextDisabled(UiLocale.Get("S6D56C77641AB6738"));
        ImGui.Spacing();

        // Disable All XA Mods
        var disableMods = cfg.TitleBarFavDisableAllModsEnabled;
        if (UiNative.Checkbox("##favDisableMods", ref disableMods))
        {
            cfg.TitleBarFavDisableAllModsEnabled = disableMods;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel(UiLocale.Native("SB211664F7A5A3C70"));

        ImGui.Spacing();

        // Fav Mod List
        var modList = cfg.TitleBarFavModListEnabled;
        if (UiNative.Checkbox("##favModList", ref modList))
        {
            cfg.TitleBarFavModListEnabled = modList;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel(UiLocale.Native("S6AC11E07FAC5DB36"));

        if (modList)
        {
            ImGui.SameLine();
            var savedNames = plugin.GetSavedModListNames();
            var currentName = cfg.TitleBarFavModListName;
            ImGui.SetNextItemWidth(Scale(200f));
            using (var imguiScope317 = ImRaii.Combo("##favModListName", string.IsNullOrEmpty(currentName) ? UiLocale.Get("S89C3351CC641CE17") : currentName))
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
        if (UiNative.Checkbox("##favGlamWeather", ref glamWeather))
        {
            cfg.TitleBarFavGlamWeatherEnabled = glamWeather;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel(UiLocale.Native("SA515FED62A4035A1"));

        ImGui.Spacing();

        // AR Pre-Process
        var arPre = cfg.TitleBarFavArPreProcessEnabled;
        if (UiNative.Checkbox("##favArPre", ref arPre))
        {
            cfg.TitleBarFavArPreProcessEnabled = arPre;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel(UiLocale.Native("S35ADEE40AF5CA4B9"));

        ImGui.Spacing();

        // AR Post-Process
        var arPost = cfg.TitleBarFavArPostProcessEnabled;
        if (UiNative.Checkbox("##favArPost", ref arPost))
        {
            cfg.TitleBarFavArPostProcessEnabled = arPost;
            cfg.Save();
            RebuildTitleBarFavButtons();
        }
        ImGui.SameLine();
        FavRowLabel(UiLocale.Native("SDA1C3D4E0D12DF75"));
    }

    private void DrawPluginOpsFavCustomItems(Configuration cfg)
    {
        ImGui.TextDisabled(UiLocale.Get("SBD53CD58F0A7B7CC"));
        ImGui.TextDisabled(UiLocale.Get("SE065D1B8DDF0CF42"));
        ImGui.TextDisabled(UiLocale.Get("S8DA9B09C51D4192B"));
        ImGui.TextDisabled(UiLocale.Get("S166EEB2973D8EBFB"));
        ImGui.Spacing();

        var changed = false;
        var removedIndex = -1;
        var selectionOptions = GetTitleBarFavSelectionOptions();

        if (UiNative.Button(UiLocale.Native("S82F7A50E9BA42744")))
        {
            AddPluginOpsCustomFavRow();
            SaveCustomFavItems(cfg);
        }

        if (pluginOpsFavCustomEnabled.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(UiLocale.Get("SA374F5421E962C05"));
        }

        ImGui.Spacing();

        for (var i = 0; i < pluginOpsFavCustomEnabled.Count; i++)
        {
            using (ImRaii.PushId($"favCustom{i}"))
            {

                var en = pluginOpsFavCustomEnabled[i];
                if (UiNative.Checkbox($"##en{i}", ref en))
                {
                    pluginOpsFavCustomEnabled[i] = en;
                    changed = true;
                }
                ImGui.SameLine();

                ImGui.SetNextItemWidth(Scale(220f));
                var comboLabel = pluginOpsFavCustomMenuInputs[i].Length > 0
                    ? GetTitleBarFavSelectionLabel(pluginOpsFavCustomMenuInputs[i])
                    : UiLocale.Get("SD9E10EF8B9864688");
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
                    ImGui.InputTextWithHint($"##menuSearch{i}", UiLocale.Get("S9517C93CCF98B1C5"), ref searchInput, 128);
                    pluginOpsFavCustomSearchInputs[i] = searchInput;

                    ImGui.Separator();

                    if (UiNative.Selectable(UiLocale.Native("S552E423045FCBD94"), string.IsNullOrEmpty(pluginOpsFavCustomMenuInputs[i])))
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

                            ImGui.TextDisabled(UiLocale.T(option.Category));
                            lastCategory = option.Category;
                        }

                        var selected = pluginOpsFavCustomMenuInputs[i].Equals(option.Key, StringComparison.OrdinalIgnoreCase);
                        if (UiNative.Selectable(option.Label, selected, display: GetLocalizedFavoriteLabel(option)))
                        {
                            pluginOpsFavCustomMenuInputs[i] = option.Key;
                            pluginOpsFavCustomSearchInputs[i] = string.Empty;
                            changed = true;
                        }
                        if (selected) ImGui.SetItemDefaultFocus();
                    }

                    if (!matchesFound)
                        ImGui.TextDisabled(UiLocale.Get("SD6572BD2E77380CE"));


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
        ImGui.TextDisabled(UiLocale.Get("S764E0B1A3C91E3D5"));
        ImGui.TextDisabled(UiLocale.Get("SAA91DE9D8FED94FD"));
        ImGui.Spacing();

        var changed = false;

        for (var i = 0; i < MaxResolutionFavItems; i++)
        {
            using (ImRaii.PushId($"favRes{i}"))
            {

                var en = pluginOpsFavResEnabled[i];
                if (UiNative.Checkbox($"##en{i}", ref en))
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
                ImGui.TextDisabled(UiLocale.Get("S2D711642B726B044"));
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
                FavRowLabel(UiLocale.FormatKey("SA1F512BC7534D4F3", pluginOpsFavResWidthInputs[i], pluginOpsFavResHeightInputs[i]));

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
