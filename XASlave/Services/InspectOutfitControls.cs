using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace XASlave.Services;

internal readonly record struct InspectOutfitGeometry(float X, float Y, float Width)
{
    internal static InspectOutfitGeometry Create(float x, float y, float width, float hostWidth, float hostHeight)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(hostWidth) || !float.IsFinite(hostHeight)
            || x < 0 || y < 0 || width <= 0 || hostWidth <= 0 || hostHeight <= 0 || x + width > hostWidth || y + 52 > hostHeight)
            throw new InvalidOperationException("CharacterInspect has no compatible space for its try-on button.");
        return new(x, y + 28, width);
    }
}

// One owned child and one conditionally restored anchor height. All raw pointers
// are resolved again through the live addon key; no cached pointer is dereferenced
// after a host epoch ends.
internal sealed unsafe class InspectOutfitControls : IDisposable
{
    private readonly TextButtonNode button;
    private readonly Func<bool> hostAlive;
    private readonly ushort originalHeight;
    private bool retired, changedHeight;
    internal InspectOutfitHost Host { get; }

    internal static InspectOutfitHost? Resolve(long epoch, bool requireVisible = true)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var addon = AddonHelper.GetAddon("CharacterInspect");
        if (addon == null || !addon->IsFullyLoaded() || addon->RootNode == null || addon->Id == 0
            || (requireVisible && !addon->IsVisible)) return null;
        var anchor = addon->GetNodeById(6);
        if (anchor == null) return null;
        return new((nint)addon, addon->Id, (nint)addon->RootNode, (nint)anchor, epoch);
    }

    internal InspectOutfitControls(InspectOutfitHost host, Func<bool> hostAlive, Action clicked)
    {
        Host = host; this.hostAlive = hostAlive;
        Require(true);
        var anchor = (AtkResNode*)host.Anchor;
        originalHeight = anchor->Height;
        var geometry = Geometry();
        button = new TextButtonNode();
        try
        {
            button.String = "Try On All";
            button.Position = new Vector2(geometry.X, geometry.Y); button.Size = new Vector2(geometry.Width, 24);
            button.OnClick = () => { if (!retired && hostAlive() && Resolve(Host.Generation) == Host) clicked(); };
            Require(true);
            ((AtkResNode*)Host.Anchor)->Height = 24; changedHeight = true;
            button.AttachNode((AtkResNode*)Host.Root);
        }
        catch
        {
            retired = true; button.OnClick = null;
            try { button.Dispose(); }
            finally { RestoreHeight(); }
            throw;
        }
    }

    private void Require(bool visible)
    {
        if (!hostAlive() || Resolve(Host.Generation, visible) != Host)
            throw new InvalidOperationException("The inspection window generation changed.");
    }

    private InspectOutfitGeometry Geometry()
    {
        Require(true);
        var root = (AtkResNode*)Host.Root; var anchor = (AtkResNode*)Host.Anchor;
        return InspectOutfitGeometry.Create(anchor->X, anchor->Y, anchor->Width, root->Width, root->Height);
    }

    internal void Update(bool busy, bool canStart)
    {
        if (retired) return;
        Require(false);
        if (Resolve(Host.Generation) == null) { button.IsVisible = false; return; }
        if (((AtkResNode*)Host.Anchor)->Height != 24)
            throw new InvalidOperationException("The inspection anchor was resized by another owner.");
        var geometry = Geometry();
        button.Position = new Vector2(geometry.X, geometry.Y); button.Size = new Vector2(geometry.Width, 24);
        button.String = busy ? "Stop" : "Try On All";
        button.IsEnabled = busy || canStart; button.IsVisible = true;
    }

    internal void StopCallbacks()
    {
        retired = true;
        button.OnClick = null;
    }

    private void RestoreHeight()
    {
        if (!changedHeight) return;
        changedHeight = false;
        if (!hostAlive() || Resolve(Host.Generation, false) != Host) return;
        var anchor = (AtkResNode*)Host.Anchor;
        if (anchor->Height == 24) anchor->Height = originalHeight;
    }

    public void Dispose()
    {
        StopCallbacks();
        try { button.Dispose(); }
        finally { RestoreHeight(); }
    }
}
