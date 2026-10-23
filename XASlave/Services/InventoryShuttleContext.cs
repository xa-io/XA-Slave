using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

internal readonly record struct InventoryShuttleAddon(string Name, long Epoch, nint Address, uint Id);
internal readonly record struct InventoryShuttleWorld(ulong Actor, ulong Content, uint Territory, ulong Retainer);
internal readonly record struct InventoryShuttleAccess(InventoryShuttleWorld World, InventoryShuttleAddon Source, InventoryShuttleAddon Destination);

// Framework-only lifecycle stamps. Visible cached inventory memory is never access authority.
internal sealed unsafe class InventoryShuttleContext : IDisposable
{
    private static readonly string[] Names = ["Inventory", "InventoryLarge", "InventoryExpansion", "InventoryBuddy", "InventoryBuddy2", "InventoryRetainer", "InventoryRetainerLarge", "ContextMenu"];
    private readonly IAddonLifecycle lifecycle;
    private readonly Action revoked;
    private readonly Dictionary<string, InventoryShuttleAddon> observed = new(StringComparer.Ordinal);
    private readonly HashSet<InventoryShuttleAddon> claimed = [];
    private InventoryShuttleWorld? world;
    private InventoryShuttleAddon? ownedMenu;
    private long epoch;
    private bool disposed;

    internal InventoryShuttleContext(IAddonLifecycle lifecycle, Action revoked)
    {
        this.lifecycle = lifecycle; this.revoked = revoked;
        try
        {
            foreach (var name in Names)
            {
                lifecycle.RegisterListener(AddonEvent.PostSetup, name, Observe);
                lifecycle.RegisterListener(AddonEvent.PreFinalize, name, Observe);
                var addon = AddonHelper.GetAddon(name);
                if (addon != null) observed[name] = new(name, 0, (nint)addon, addon->Id);
            }
            Plugin.Framework.Update += Update;
        }
        catch { lifecycle.UnregisterListener(Observe); throw; }
    }

    internal void Reset()
    {
        claimed.Clear(); world = null; ownedMenu = null;
    }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if (disposed) return;
        var pointer = (AtkUnitBase*)args.Addon.Address;
        observed[args.AddonName] = new(args.AddonName, ++epoch,
            kind == AddonEvent.PostSetup ? args.Addon.Address : 0,
            kind == AddonEvent.PostSetup && pointer != null ? pointer->Id : 0u);
        if (args.AddonName == "ContextMenu" && kind == AddonEvent.PostSetup && ownedMenu != null)
        { Revoke(); return; }
        foreach (var stamp in claimed)
            if (stamp.Name == args.AddonName) { Revoke(); break; }
    }

    internal InventoryShuttleAccess Capture(string source, string destination, bool requiresRetainer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = ReadWorld();
        if (requiresRetainer && current.Retainer == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7DBDBD91609CCD46"), originalMessage => new InvalidOperationException(originalMessage));
        if (world != null && world != current) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1EC21D30ED135F2A"), originalMessage => new InvalidOperationException(originalMessage));
        var from = RequireReady(source); var to = RequireReady(destination);
        world = current; claimed.Add(from); claimed.Add(to);
        return new(current, from, to);
    }

    internal void Require(InventoryShuttleAccess access)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (world != access.World || ReadWorld() != access.World || !IsCurrent(access.Source) || !IsCurrent(access.Destination))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4375C35C6A6C69F2"), originalMessage => new InvalidOperationException(originalMessage));
    }

    private InventoryShuttleAddon RequireReady(string name)
    {
        if (!observed.TryGetValue(name, out var stamp) || !IsCurrent(stamp))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SFDEBBB74E55AE9A6"), global::XASlave.Localization.UiMessage.Raw(name)), originalMessage => new InvalidOperationException(originalMessage));
        return stamp;
    }

    internal bool IsReady(string name) => observed.TryGetValue(name, out var stamp) && IsCurrent(stamp);

    internal void BindMenu()
    {
        if (ownedMenu == null && observed.TryGetValue("ContextMenu", out var stamp) && IsCurrent(stamp)) ownedMenu = stamp;
    }

    internal void CloseOwnedMenu()
    {
        if (ownedMenu is not { } stamp || !IsCurrent(stamp)) return;
        // Identity is checked on the framework immediately before the close call.
        AddonHelper.CloseAddon(stamp.Name);
    }

    private bool IsCurrent(InventoryShuttleAddon stamp)
    {
        if (disposed || !observed.TryGetValue(stamp.Name, out var current) || current != stamp || stamp.Address == 0) return false;
        var pointer = AddonHelper.GetAddon(stamp.Name);
        return pointer != null && (nint)pointer == stamp.Address && pointer->Id == stamp.Id
            && pointer->IsReady && pointer->IsVisible && pointer->RootNode != null;
    }

    private static InventoryShuttleWorld ReadWorld()
    {
        Plugin.AssertGameThread();
        var player = Plugin.ObjectTable.LocalPlayer;
        var input = UIInputData.Instance();
        if (!Plugin.PlayerState.IsLoaded || player == null || input == null || !input->CursorInputs.IsGameWindowFocused
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SA05ECE1F02E898F1"), originalMessage => new InvalidOperationException(originalMessage));
        var character = (Character*)player.Address;
        if (character == null || character->ContentId == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB70311A1C08EA833"), originalMessage => new InvalidOperationException(originalMessage));
        var manager = RetainerManager.Instance();
        var retainer = manager == null ? null : manager->GetActiveRetainer();
        return new(player.GameObjectId, character->ContentId, Plugin.ClientState.TerritoryType, retainer == null ? 0 : retainer->RetainerId);
    }

    private void Update(IFramework framework)
    {
        if (disposed || world == null) return;
        try
        {
            if (ReadWorld() != world) { Revoke(); return; }
            foreach (var stamp in claimed) if (!IsCurrent(stamp)) { Revoke(); return; }
        }
        catch { Revoke(); }
    }

    private void Revoke() { Reset(); revoked(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Reset(); observed.Clear();
        Plugin.Framework.Update -= Update;
        lifecycle.UnregisterListener(Observe);
    }
}
