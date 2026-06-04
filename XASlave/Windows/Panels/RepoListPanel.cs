using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

/// <summary>
/// Repo List panel - curated repository links and plugin presence checks for XA Slave.
/// </summary>
public partial class SlaveWindow
{
    private const string AethertekRepoUrl = "https://aethertek.io/x.json";
    private const string PunishRepoUrl = "https://love.puni.sh/ment.json";
    private const string KawaiiRepoUrl = "https://puni.sh/api/repository/kawaii";
    private const string NightmareRepoUrl = "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json";
    private const string VeynRepoUrl = "https://puni.sh/api/repository/veyn";
    private const string VeraRepoUrl = "https://puni.sh/api/repository/vera";

    private DateTime repoListStatusExpiry = DateTime.MinValue;
    private string repoListStatus = string.Empty;

    private void DrawRepoList()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Repo List");
        ImGui.TextDisabled("Helpful and commonly required repositories for XA Slave integrations.");
        ImGui.TextWrapped("Rows include every plugin currently published by the referenced repositories. Status shows whether Dalamud reports the plugin installed or directly available.");

        if (!string.IsNullOrWhiteSpace(repoListStatus) && DateTime.UtcNow <= repoListStatusExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), repoListStatus);
        }

        ImGui.Spacing();
        if (ImGui.Button("Open Plugin Installer"))
            ChatHelper.SendMessage("/xlplugins");

        ImGui.SameLine();
        if (ImGui.Button("Open Plugin Settings"))
            ChatHelper.SendMessage("/xlsettings");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawRepositoryTable(
            RepoPlugin("Aethertek", "XA", "XA Database", AethertekRepoUrl, plugin.IpcClient.IsXaDatabaseAvailable() || IsPluginInstalled("XADatabase", "XA Database")),
            RepoPlugin("Aethertek", "XA", "XA Slave", AethertekRepoUrl, true),
            RepoPlugin("Aethertek", "XA", "XA HUD Navigator", AethertekRepoUrl, "XAHudNavigator"),
            RepoPlugin("Aethertek", "DhogGPT", "Fren Rider", AethertekRepoUrl, "FrenRider"),
            RepoPlugin("Aethertek", "DhogGPT", "Loot Goblin", AethertekRepoUrl, "LootGoblin"),
            RepoPlugin("Aethertek", "DhogGPT", "M.O.G.T.O.M.E.", AethertekRepoUrl, "MOGTOME"),
            RepoPlugin("Aethertek", "DhogGPT", "Vermaxion", AethertekRepoUrl, "VERMAXION"),
            RepoPlugin("Aethertek", "DhogGPT", "Hello Fellow Human", AethertekRepoUrl, "HelloFellowHuman"),
            RepoPlugin("Aethertek", "DhogGPT", "Chocobo Colourized", AethertekRepoUrl, "ChocoboColourized"),
            RepoPlugin("Aethertek", "DhogGPT", "DhogGPT", AethertekRepoUrl),
            RepoPlugin("Aethertek", "DhogGPT", "Dhog Potato System", AethertekRepoUrl, "DPS"),
            RepoPlugin("Aethertek", "DhogGPT", "Dheacon", AethertekRepoUrl, "dheacon"),
            RepoPlugin("Aethertek", "DhogGPT, XA", "Krangler", AethertekRepoUrl),
            RepoPlugin("Aethertek", "DhogGPT", "Choke-abo", AethertekRepoUrl, "ChokeAbo"),
            RepoPlugin("Aethertek", "DhogGPT", "Thick Thighs Save Lives", AethertekRepoUrl, "TTSL"),
            RepoPlugin("Aethertek", "DhogGPT", "Botology", AethertekRepoUrl, "botology"),
            RepoPlugin("Aethertek", "DhogGPT", "BFE", AethertekRepoUrl),
            RepoPlugin("Aethertek", "DhogGPT", "Coppelia", AethertekRepoUrl),
            RepoPlugin("Aethertek", "DhogGPT", "Footballer", AethertekRepoUrl, "footballer"),
            RepoPlugin("Aethertek", "DhogGPT", "AI Duty Solver", AethertekRepoUrl, "ADS"),
            RepoPlugin("Punish Studio", "kawaii, NightmareXIV", "Orbwalker", PunishRepoUrl, "Orbwalker"),
            RepoPlugin("Punish Studio", "Team Wrath", "Wrath Combo", PunishRepoUrl, "WrathCombo"),
            RepoPlugin("Punish Studio", "Taurenkey, Kage, NightmareXIV", "Saucy", PunishRepoUrl, "Saucy"),
            RepoPlugin("Punish Studio", "daemitus, kawaii", "Yes Already", PunishRepoUrl, plugin.IpcClient.IsYesAlreadyAvailable() || IsPluginInstalled("YesAlready", "Yes Already")),
            RepoPlugin("Punish Studio", "Taurenkey, NightmareXIV, croizat", "Pandora's Box", PunishRepoUrl, plugin.IpcClient.IsPandorasBoxAvailable() || IsPluginInstalled("PandorasBox", "Pandora's Box")),
            RepoPlugin("Punish Studio", "kawaii, NightmareXIV", "Avarice", PunishRepoUrl, "Avarice"),
            RepoPlugin("Punish Studio", "kawaii, NightmareXIV", "AutoRetainer", PunishRepoUrl, plugin.IpcClient.IsAutoRetainerAvailable() || IsPluginInstalled("AutoRetainer")),
            RepoPlugin("Punish Studio", "kawaii, jukka", "Palace Pal", PunishRepoUrl, "PalacePal"),
            RepoPlugin("Punish Studio", "Taurenkey, Gid, NightmareXIV", "LazyLoot", PunishRepoUrl, "LazyLoot"),
            RepoPlugin("Punish Studio", "Det, Taurenkey", "AutoHook", PunishRepoUrl, "AutoHook"),
            RepoPlugin("Punish Studio", "Taurenkey, kawaii, NightmareXIV", "Artisan", PunishRepoUrl, plugin.IpcClient.IsArtisanAvailable() || IsPluginInstalled("Artisan")),
            RepoPlugin("Punish Studio", "Chalkos, NightmareXIV", "Marketbuddy", PunishRepoUrl, "Marketbuddy"),
            RepoPlugin("Punish Studio", "NightmareXIV", "Splatoon", PunishRepoUrl, plugin.IpcClient.IsSplatoonAvailable() || IsPluginInstalled("Splatoon")),
            RepoPlugin("Punish Studio", "Liza Carvelli & various contributors", "Questionable", PunishRepoUrl, "Questionable"),
            RepoPlugin("Kawaii", "kawaii", "Dropbox", KawaiiRepoUrl, plugin.IpcClient.IsDropboxAvailable() || IsPluginInstalled("Dropbox")),
            RepoPlugin("Kawaii", "kawaii, NightmareXIV", "Hyperborea", KawaiiRepoUrl, "Hyperborea"),
            RepoPlugin("Kawaii", "kawaii", "Chocoholic", KawaiiRepoUrl, "Chocoholic"),
            RepoPlugin("NightmareXIV", "NightmareXIV, AsunaTsuki", "DynamicBridge", NightmareRepoUrl, "DynamicBridge"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "HuntTrainAssistant", NightmareRepoUrl, "HuntTrainAssistant"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "Lifestream", NightmareRepoUrl, plugin.IpcClient.IsLifestreamAvailable() || IsPluginInstalled("Lifestream")),
            RepoPlugin("NightmareXIV", "NightmareXIV", "DSR Toolbox", NightmareRepoUrl, "DSREyeLocator"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "WeathermanExtras", NightmareRepoUrl, "WExtras"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "TextAdvance", NightmareRepoUrl, plugin.IpcClient.IsTextAdvanceAvailable() || IsPluginInstalled("TextAdvance")),
            RepoPlugin("NightmareXIV", "NightmareXIV", "AntiAfkKick", NightmareRepoUrl, "AntiAfkKick-Dalamud"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "SelectString", NightmareRepoUrl, "SelectString"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "Prioritizer", NightmareRepoUrl, "Prioritizer"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "UnloadErrorFuckOff", NightmareRepoUrl, "UnloadErrorFuckOff"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "Imagenation", NightmareRepoUrl, "Imagenation"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "ObjectExplorer", NightmareRepoUrl, "ObjectExplorer"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "StandalonePluginManager", NightmareRepoUrl, "StandalonePluginManager"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "Battlevest", NightmareRepoUrl, "Battlevest"),
            RepoPlugin("NightmareXIV", "NightmareXIV", "Stylist", NightmareRepoUrl, "Stylist"),
            RepoPlugin("NightmareXIV", "NightmareXIV, Marulu, StreetRat", "MaskedCarnivale", NightmareRepoUrl, "MaskedCarnivale"),
            RepoPlugin("Veyn & xan_0", "veyn, croizat", "Satisfier", VeynRepoUrl, "vsatisfy"),
            RepoPlugin("Veyn & xan_0", "veyn, croizat", "V(ery) Island", VeynRepoUrl, "visland"),
            RepoPlugin("Veyn & xan_0", "veyn", "Easier Faux Hollows", VeynRepoUrl, "vfaux"),
            RepoPlugin("Veyn & xan_0", "veyn, xan_0", "Boss Mod", VeynRepoUrl, "BossMod"),
            RepoPlugin("Veyn & xan_0", "veyn, croizat", "vfallguy", VeynRepoUrl, "vfallguy"),
            RepoPlugin("Veyn & xan_0", "veyn, xan_0", "vnavmesh", VeynRepoUrl, plugin.IpcClient.IsVnavAvailable() || IsPluginInstalled("vnavmesh")),
            RepoPlugin("Vera", "vera.lyn", "Deliveroo", VeraRepoUrl, plugin.IpcClient.IsDeliverooAvailable() || IsPluginInstalled("Deliveroo")),
            RepoPlugin("Vera", "vera.lyn", "Squadronista", VeraRepoUrl, "Squadronista"),
            RepoPlugin("Vera", "vera.lyn", "Workshoppa", VeraRepoUrl, "Workshoppa"),
            RepoPlugin("Vera", "vera.lyn", "Discard Helper", VeraRepoUrl, "ARDiscard"),
            RepoPlugin("Vera", "vera.lyn", "Gearsetter", VeraRepoUrl, "Gearsetter"),
            RepoPlugin("Vera", "vera.lyn", "Fish Notify", VeraRepoUrl, "FishNotify"),
            RepoPlugin("Vera", "vera.lyn", "Vera's Integrated World Improvements", VeraRepoUrl, "VIWI"),
            RepoPlugin("Vera", "vera.lyn", "KitchenSink", VeraRepoUrl, "KitchenSink"));
    }

    private RepoListEntry RepoPlugin(string group, string author, string pluginName, string repoUrl, bool installed)
        => new(group, author, pluginName, repoUrl, installed);

    private RepoListEntry RepoPlugin(string group, string author, string pluginName, string repoUrl, params string[] candidates)
        => new(group, author, pluginName, repoUrl, IsPluginInstalled(candidates.Concat(new[] { pluginName }).ToArray()));

    private void DrawRepositoryTable(params RepoListEntry[] entries)
    {
        if (ImGui.BeginTable("RepoPlugins", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable))
        {
            ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.35f);
            ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthStretch, 1.05f);
            ImGui.TableSetupColumn("Plugin Name", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.85f);
            ImGui.TableSetupColumn("Copy Repo Button", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.0f);
            ImGui.TableHeadersRow();

            var sortedEntries = SortRepositoryEntries(entries);
            for (var i = 0; i < sortedEntries.Length; i++)
            {
                var entry = sortedEntries[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextWrapped(entry.Group);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(entry.Author);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(entry.PluginName);

                ImGui.TableNextColumn();
                if (entry.Installed)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Installed");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Not installed");

                ImGui.TableNextColumn();
                if (string.IsNullOrWhiteSpace(entry.RepoUrl))
                {
                    ImGui.TextDisabled("Configured repo");
                }
                else if (ImGui.SmallButton($"Copy##RepoListCopy{i}"))
                {
                    ImGui.SetClipboardText(entry.RepoUrl);
                    repoListStatus = $"Copied {entry.Author} repo URL to clipboard.";
                    repoListStatusExpiry = DateTime.UtcNow.AddSeconds(5);
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.IsNullOrWhiteSpace(entry.RepoUrl) ? "Install from your configured plugin repositories." : entry.RepoUrl);
            }

            ImGui.EndTable();
        }
    }

    private static RepoListEntry[] SortRepositoryEntries(RepoListEntry[] entries)
    {
        var sortedEntries = entries.ToArray();
        var sortColumn = RepoListSortColumn.Default;
        var sortDescending = false;

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty)
            sortSpecs.SpecsDirty = false;

        if (sortSpecs.SpecsCount > 0)
        {
            unsafe
            {
                var spec = sortSpecs.Specs;
                sortColumn = spec.ColumnIndex switch
                {
                    0 => RepoListSortColumn.Group,
                    1 => RepoListSortColumn.Author,
                    2 => RepoListSortColumn.PluginName,
                    3 => RepoListSortColumn.Status,
                    _ => RepoListSortColumn.Default,
                };
                sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
            }
        }

        Array.Sort(sortedEntries, (left, right) => CompareRepositoryEntries(left, right, sortColumn, sortDescending));
        return sortedEntries;
    }

    private static int CompareRepositoryEntries(RepoListEntry left, RepoListEntry right, RepoListSortColumn sortColumn, bool sortDescending)
    {
        var compare = sortColumn switch
        {
            RepoListSortColumn.Group => CompareDefaultRepositoryOrder(left, right),
            RepoListSortColumn.Author => CompareByAuthor(left, right),
            RepoListSortColumn.PluginName => CompareByPluginName(left, right),
            RepoListSortColumn.Status => string.Compare(StatusLabel(left), StatusLabel(right), StringComparison.OrdinalIgnoreCase),
            _ => CompareDefaultRepositoryOrder(left, right),
        };

        if (sortDescending)
            compare = -compare;

        if (compare == 0 && sortColumn != RepoListSortColumn.Default && sortColumn != RepoListSortColumn.Group)
            compare = CompareDefaultRepositoryOrder(left, right);

        return compare;
    }

    private static int CompareDefaultRepositoryOrder(RepoListEntry left, RepoListEntry right)
    {
        var compare = string.Compare(left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        compare = GetDefaultAuthorPriority(left).CompareTo(GetDefaultAuthorPriority(right));
        if (compare != 0)
            return compare;

        compare = CompareByPluginName(left, right);
        if (compare != 0)
            return compare;

        return CompareByAuthor(left, right);
    }

    private static int CompareByAuthor(RepoListEntry left, RepoListEntry right)
    {
        var compare = string.Compare(left.Author, right.Author, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        compare = CompareByPluginName(left, right);
        if (compare != 0)
            return compare;

        return string.Compare(left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareByPluginName(RepoListEntry left, RepoListEntry right)
    {
        var compare = string.Compare(left.PluginName, right.PluginName, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        compare = string.Compare(left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        return string.Compare(left.Author, right.Author, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetDefaultAuthorPriority(RepoListEntry entry)
    {
        if (!entry.Group.Equals("Aethertek", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (entry.Author.Equals("XA", StringComparison.OrdinalIgnoreCase))
            return 0;

        return entry.Author.Contains("Dhog", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static string StatusLabel(RepoListEntry entry)
        => entry.Installed ? "Installed" : "Not installed";

    private readonly struct RepoListEntry
    {
        public RepoListEntry(string group, string author, string pluginName, string repoUrl, bool installed)
        {
            Group = group;
            Author = author;
            PluginName = pluginName;
            RepoUrl = repoUrl;
            Installed = installed;
        }

        public string Group { get; }
        public string Author { get; }
        public string PluginName { get; }
        public string RepoUrl { get; }
        public bool Installed { get; }
    }

    private enum RepoListSortColumn
    {
        Default,
        Group,
        Author,
        PluginName,
        Status,
    }

    private static bool IsPluginInstalled(params string[] candidates)
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(installedPlugin =>
                candidates.Any(candidate =>
                    installedPlugin.InternalName.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    installedPlugin.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }
}
