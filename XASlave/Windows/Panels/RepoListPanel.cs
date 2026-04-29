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
    private DateTime repoListStatusExpiry = DateTime.MinValue;
    private string repoListStatus = string.Empty;

    private void DrawRepoList()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Repo List");
        ImGui.TextDisabled("Helpful and commonly required repositories for XA Slave integrations.");
        ImGui.TextWrapped("Status checks focus on the plugins XA Slave integrates with directly. Some repositories contain additional plugins beyond the list shown here.");

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

        DrawRepositoryBlock(
            "Aethertek",
            "XA & Dhog Plugins",
            "https://aethertek.io/x.json",
            ("XA Slave", true),
            ("XA Database", plugin.IpcClient.IsXaDatabaseAvailable()));

        DrawRepositoryBlock(
            "Punish Studio",
            "AutoRetainer, YesAlready, Pandora's Box, Artisan, Splatoon",
            "https://love.puni.sh/ment.json",
            ("AutoRetainer", plugin.IpcClient.IsAutoRetainerAvailable()),
            ("YesAlready", plugin.IpcClient.IsYesAlreadyAvailable()),
            ("Pandora's Box", plugin.IpcClient.IsPandorasBoxAvailable()),
            ("Artisan", plugin.IpcClient.IsArtisanAvailable()),
            ("Splatoon", plugin.IpcClient.IsSplatoonAvailable()));

        DrawRepositoryBlock(
            "Kawaii",
            "Dropbox",
            "https://puni.sh/api/repository/kawaii",
            ("Dropbox", plugin.IpcClient.IsDropboxAvailable()));

        DrawRepositoryBlock(
            "NightmareXIV",
            "Lifestream, TextAdvance",
            "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json",
            ("Lifestream", plugin.IpcClient.IsLifestreamAvailable()),
            ("TextAdvance", plugin.IpcClient.IsTextAdvanceAvailable()));

        DrawRepositoryBlock(
            "Veyn & xan_0",
            "vnavmesh",
            "https://puni.sh/api/repository/veyn",
            ("vnavmesh", plugin.IpcClient.IsVnavAvailable()));

        DrawRepositoryBlock(
            "Vera",
            "Workshoppa, Deliveroo",
            "https://puni.sh/api/repository/vera",
            ("Workshoppa", IsInstalledPluginLoaded("Workshoppa")),
            ("Deliveroo", plugin.IpcClient.IsDeliverooAvailable()));
    }

    private void DrawRepositoryBlock(string owner, string pluginsLabel, string repoUrl, params (string Name, bool Installed)[] plugins)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.50f, 1.0f));
        ImGui.TextWrapped(owner);
        ImGui.PopStyleColor();
        ImGui.TextWrapped(pluginsLabel);
        ImGui.TextWrapped(repoUrl);

        if (ImGui.SmallButton($"Copy URL##{owner}"))
        {
            ImGui.SetClipboardText(repoUrl);
            repoListStatus = $"Copied {owner} repo URL to clipboard.";
            repoListStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }

        if (ImGui.BeginTable($"RepoPlugins##{owner}", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            foreach (var (name, installed) in plugins)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextWrapped(name);
                ImGui.TableNextColumn();
                if (installed)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Installed");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Not installed");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    private static bool IsInstalledPluginLoaded(params string[] candidates)
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(installedPlugin =>
                installedPlugin.IsLoaded &&
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
