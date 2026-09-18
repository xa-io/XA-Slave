using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

internal readonly record struct FurnitureAddonStamp(string Name, long Epoch, nint Address, uint Id);

// Subscribe before attaching XA controls. PreFinalize invalidates scalar stamps
// before invoking the owner; no old addon is dereferenced during invalidation.
internal sealed unsafe class FurnitureRestoreContext : IDisposable
{
    private static readonly string[] Names = ["HousingGoods", "SelectYesno"];
    private readonly IAddonLifecycle lifecycle;
    private readonly Action<string> revoked;
    private readonly Dictionary<string, FurnitureAddonStamp> observed = new(StringComparer.Ordinal);
    private FurnitureAddonStamp? host;
    private Func<bool>? currentSession;
    private bool disposed;
    internal long Epoch { get; private set; }
    internal long FrameworkSequence { get; private set; }

    internal FurnitureRestoreContext(IAddonLifecycle lifecycle, Action<string> revoked)
    {
        this.lifecycle = lifecycle; this.revoked = revoked;
        try
        {
            foreach (var name in Names)
            {
                lifecycle.RegisterListener(AddonEvent.PreFinalize, name, Observe);
                lifecycle.RegisterListener(AddonEvent.PostSetup, name, Observe);
                var addon = AddonHelper.GetAddon(name);
                if (addon != null) observed[name] = new(name, ++Epoch, (nint)addon, addon->Id);
            }
            Plugin.Framework.Update += Update;
        }
        catch { lifecycle.UnregisterListener(Observe); Plugin.Framework.Update -= Update; throw; }
    }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if (disposed) return;
        var address = kind == AddonEvent.PostSetup ? args.Addon.Address : 0;
        observed[args.AddonName] = new(args.AddonName, ++Epoch, address, address == 0 ? 0u : ((AtkUnitBase*)address)->Id);
        if (args.AddonName == "HousingGoods" && host != null) Revoke("The housing furniture interface was replaced or finalized.");
    }

    internal FurnitureAddonStamp? Fresh(string name)
    {
        if (disposed || !observed.TryGetValue(name, out var stamp) || stamp.Address == 0) return null;
        var addon = AddonHelper.GetAddon(name);
        return addon != null && (nint)addon == stamp.Address && addon->Id == stamp.Id && addon->IsVisible && addon->IsReady && addon->RootNode != null ? stamp : null;
    }

    // An opener can return before PostSetup/readiness. The sequence owns the
    // finite warning deadline; a finalized, hidden or replaced addon never waits.
    internal bool WarningInitializing(uint id, long floor)
    {
        if (disposed) return false;
        if (!observed.TryGetValue("SelectYesno", out var stamp) || stamp.Epoch <= floor) return true;
        if (stamp.Address == 0 || stamp.Id != id) return false;
        var addon = AddonHelper.GetAddon("SelectYesno");
        return addon != null && (nint)addon == stamp.Address && addon->Id == id && addon->IsVisible && !addon->IsReady;
    }

    internal FurnitureAddonStamp ClaimHost()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (host != null) throw new InvalidOperationException("The furniture interface already owns a batch.");
        host = Fresh("HousingGoods") ?? throw new InvalidOperationException("Open the native HousingGoods editing interface first.");
        return host.Value;
    }

    internal void BindSession(Func<bool> isCurrent)
    {
        RequireHost();
        currentSession = isCurrent;
    }

    internal FurnitureAddonStamp RequireHost()
    {
        if (disposed || host is not { } stamp || Fresh("HousingGoods") != stamp)
            throw new InvalidOperationException("The owned furniture interface is hidden, closed or replaced.");
        return stamp;
    }

    internal void End() { host = null; currentSession = null; }

    private void Update(IFramework framework)
    {
        if (disposed) return;
        FrameworkSequence++;
        if (host == null) return;
        try
        {
            RequireHost();
            if (!Plugin.PlayerState.IsLoaded || (currentSession?.Invoke() == false))
                Revoke("The character, estate or housing edit session changed.");
        }
        catch (Exception error) { if (host != null) Revoke(error.Message); }
    }

    private void Revoke(string reason) { End(); revoked(reason); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var active = host != null; End();
        try { if (active) revoked("Furniture interface observation disposed."); }
        finally { lifecycle.UnregisterListener(Observe); Plugin.Framework.Update -= Update; }
    }
}
