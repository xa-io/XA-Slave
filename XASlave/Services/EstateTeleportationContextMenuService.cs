using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace XASlave.Services;

/// <summary>Opens the game's friend estate selector without choosing a destination.</summary>
public sealed unsafe class EstateTeleportationContextMenuService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IPluginLog log;
    private bool enabled;
    private bool disposed;
    private uint generation;

    public EstateTeleportationContextMenuService(IContextMenu contextMenu, IPluginLog log)
    {
        this.contextMenu = contextMenu;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (disposed || value == enabled)
            return enabled;

        if (value && AgentFriendlist.MemberFunctionPointers.OpenFriendEstateTeleportation == null)
        {
            StatusText = "Unavailable - the game's estate teleportation function could not be resolved.";
            return false;
        }

        generation++;
        enabled = value;
        if (value)
        {
            contextMenu.OnMenuOpened += OnMenuOpened;
            Plugin.ClientState.Logout += OnLogout;
            Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        }
        else
        {
            contextMenu.OnMenuOpened -= OnMenuOpened;
            Plugin.ClientState.Logout -= OnLogout;
            Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        }
        StatusText = value ? "Enabled - eligible friends on your current world." : "Disabled";
        return enabled;
    }

    public void Dispose()
    {
        SetEnabled(false);
        disposed = true;
    }

    private void OnLogout(int _, int __) => generation++;

    private void OnTerritoryChanged(uint _) => generation++;

    private static bool IsReady()
        => Plugin.Framework.IsInFrameworkUpdateThread
            && Plugin.ClientState.IsLoggedIn
            && Plugin.PlayerState.IsLoaded
            && Plugin.ObjectTable.LocalPlayer != null
            && !Plugin.Condition[ConditionFlag.BetweenAreas]
            && !Plugin.Condition[ConditionFlag.BetweenAreas51];

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!enabled || !IsReady() || args.MenuType != ContextMenuType.Default
            || args.AddonName == "FriendList" || args.Target is not MenuTargetDefault target)
            return;

        try
        {
            // Never retain MenuTargetDefault: its properties read mutable native menu state.
            var name = target.TargetName;
            var world = target.TargetHomeWorld.RowId;
            var contentId = ResolveFriend(target.TargetContentId, name, world);
            if (contentId == 0)
                return;

            var owner = Plugin.PlayerState.ContentId;
            var menuGeneration = generation;
            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText("Estate Teleportation").Build(),
                PrefixChar = 'X',
                PrefixColor = 539,
                UseDefaultPrefix = false,
                OnClicked = _ => OpenEstate(contentId, name, world, owner, menuGeneration),
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[EstateTeleportation] Could not resolve the context-menu friend.");
        }
    }

    private static ulong ResolveFriend(ulong contentId, string name, uint world)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || string.IsNullOrWhiteSpace(name) || world == 0 || world >= ushort.MaxValue
            || world != local.CurrentWorld.RowId || contentId == ulong.MaxValue)
            return 0;

        var friends = InfoProxyFriendList.Instance();
        if (friends == null || friends->CharData == null || friends->EntryCount > 200)
            return 0;

        ulong match = 0;
        foreach (ref readonly var friend in friends->CharDataSpan)
        {
            if (friend.ContentId == 0 || friend.ContentId == ulong.MaxValue
                || friend.ContentId == Plugin.PlayerState.ContentId
                || friend.HomeWorld != world
                || !string.Equals(friend.NameString, name, StringComparison.Ordinal)
                || (contentId != 0 && friend.ContentId != contentId))
                continue;

            // An absent menu CID may use an exact name+world match, never a name alone.
            if (match != 0)
                return 0;
            match = friend.ContentId;
        }

        return match;
    }

    private void OpenEstate(ulong contentId, string name, uint world, ulong owner, uint menuGeneration)
    {
        if (!enabled || disposed || generation != menuGeneration || !IsReady()
            || owner == 0 || Plugin.PlayerState.ContentId != owner)
            return;

        try
        {
            if (ResolveFriend(contentId, name, world) != contentId)
            {
                StatusText = "The friend or current world changed. Reopen the player context menu.";
                return;
            }

            var agent = AgentFriendlist.Instance();
            if (agent == null || AgentFriendlist.MemberFunctionPointers.OpenFriendEstateTeleportation == null)
            {
                StatusText = "Unavailable - the game's friend estate selector is not ready.";
                return;
            }

            agent->OpenFriendEstateTeleportation(contentId);
            StatusText = "Estate window requested. Available destinations and permissions are controlled by the game.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not open estate teleportation; see the plugin log.";
            log.Warning(ex, "[EstateTeleportation] Could not open the friend estate selector.");
        }
    }
}
