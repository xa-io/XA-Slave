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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        var providerCount = CountEnabledProviders();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (providerCount == 0 ? global::XASlave.Localization.UiMessage.Key("SF86CFB55E26EAE9E") : global::XASlave.Localization.UiMessage.Format("SC191A02A73FE033C", providerCount, ((openAllEnabled && providerCount > 1 ? global::XASlave.Localization.UiMessage.Key("S55BEDA298C396769") : global::XASlave.Localization.UiMessage.Raw("."))))));
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
