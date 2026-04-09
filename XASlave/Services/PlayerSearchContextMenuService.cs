using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class PlayerSearchContextMenuService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private bool ffLogsEnabled = true;
    private bool lodestoneEnabled = true;
    private bool lalachievementsEnabled = true;
    private bool openAllEnabled = true;

    public PlayerSearchContextMenuService(
        IContextMenu contextMenu,
        IDataManager _,
        IPluginLog log)
    {
        this.contextMenu = contextMenu;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public void ApplyConfiguration(bool ffLogsEnabled, bool lodestoneEnabled, bool lalachievementsEnabled, bool openAllEnabled)
    {
        this.ffLogsEnabled = ffLogsEnabled;
        this.lodestoneEnabled = lodestoneEnabled;
        this.lalachievementsEnabled = lalachievementsEnabled;
        this.openAllEnabled = openAllEnabled;
        RefreshStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        Subscribe();
        enabled = true;
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        contextMenu.OnMenuOpened += OnMenuOpened;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        contextMenu.OnMenuOpened -= OnMenuOpened;
        subscribed = false;
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        var providerCount = CountEnabledProviders();
        StatusText = providerCount == 0
            ? "Enabled - no player search providers are selected."
            : $"Enabled - {providerCount} player search provider(s) available{(openAllEnabled && providerCount > 1 ? " with Open All." : ".")}";
    }

    private int CountEnabledProviders()
    {
        var count = 0;
        if (ffLogsEnabled)
            count++;
        if (lodestoneEnabled)
            count++;
        if (lalachievementsEnabled)
            count++;

        return count;
    }

    private bool HasEnabledProviders()
    {
        return CountEnabledProviders() > 0;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!enabled || !HasEnabledProviders() || args.MenuType != ContextMenuType.Default)
            return;

        if (args.Target is not MenuTargetDefault target)
            return;

        var player = ResolveTarget(target);
        if (player == null)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = new SeStringBuilder().AddText("XA Search").Build(),
            PrefixChar = 'X',
            PrefixColor = 539,
            UseDefaultPrefix = false,
            IsSubmenu = true,
            OnClicked = clickedArgs => clickedArgs.OpenSubmenu(BuildSubmenu(player)),
        });
    }

    private SearchTarget? ResolveTarget(MenuTargetDefault target)
    {
        var name = target.TargetName;
        var worldRow = target.TargetHomeWorld.RowId;
        var worldName = target.TargetHomeWorld.ValueNullable?.Name.ToString();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(worldName) || worldRow == 0)
            return null;

        return new SearchTarget(name, worldName, worldRow);
    }

    private IReadOnlyList<IMenuItem> BuildSubmenu(SearchTarget player)
    {
        var providers = GetEnabledProviders(player).ToList();
        if (providers.Count == 0)
            return Array.Empty<IMenuItem>();

        var items = new List<IMenuItem>();
        if (openAllEnabled && providers.Count > 1)
        {
            items.Add(BuildItem("Open All Enabled", () => OpenAllLinks(providers)));
        }

        items.AddRange(providers.Select(provider => BuildItem(provider.Name, () => OpenExternalLink(provider.Url))));
        return items;
    }

    private IEnumerable<SearchProvider> GetEnabledProviders(SearchTarget player)
    {
        if (ffLogsEnabled)
        {
            yield return new SearchProvider(
                "FFLogs",
                $"https://www.fflogs.com/search?term={Uri.EscapeDataString($"{player.Name} {player.World}")}");
        }

        if (lodestoneEnabled)
        {
            yield return new SearchProvider(
                "Lodestone",
                $"https://na.finalfantasyxiv.com/lodestone/character/?worldname={Uri.EscapeDataString(player.World)}&q={Uri.EscapeDataString(player.Name)}");
        }

        if (lalachievementsEnabled)
        {
            yield return new SearchProvider(
                "Lalachievements",
                $"https://www.lalachievements.com/characters/search?name={Uri.EscapeDataString(player.Name)}&world={Uri.EscapeDataString(player.World)}");
        }
    }

    private void OpenAllLinks(IEnumerable<SearchProvider> providers)
    {
        foreach (var provider in providers)
            OpenExternalLink(provider.Url);
    }

    private MenuItem BuildItem(string name, Action onClick)
    {
        return new MenuItem
        {
            Name = new SeStringBuilder().AddText(name).Build(),
            PrefixChar = 'X',
            PrefixColor = 539,
            UseDefaultPrefix = false,
            OnClicked = _ => onClick(),
        };
    }

    private void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to open external link: {url}");
        }
    }

    private sealed record SearchTarget(string Name, string World, uint WorldId);

    private sealed record SearchProvider(string Name, string Url);
}
