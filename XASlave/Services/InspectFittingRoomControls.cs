using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace XASlave.Services;

internal sealed unsafe class InspectFittingRoomControls : IDisposable
{
    private readonly TextButtonNode button;
    private readonly nint addon, root;
    private readonly uint id;
    private bool retired;

    internal static AtkUnitBase* Resolve()
    {
        NearbyZoneNativeBinding.RequireFramework();
        var current = AddonHelper.GetAddon("Tryon");
        return current != null && current->IsFullyLoaded() && current->IsVisible && current->RootNode != null ? current : null;
    }

    internal bool IsCurrent
    {
        get
        {
            var current = Resolve();
            return !retired && current != null && (nint)current == addon && current->Id == id && (nint)current->RootNode == root;
        }
    }

    internal InspectFittingRoomControls(Action clear)
    {
        var current = Resolve();
        if (current == null) throw new InvalidOperationException("The fitting room is not ready for its Clear button.");
        addon = (nint)current; root = (nint)current->RootNode; id = current->Id;
        button = new TextButtonNode();
        try
        {
            button.String = "Clear";
            button.Size = new Vector2(64, 24);
            button.OnClick = () => { if (IsCurrent) clear(); };
            Update();
            button.AttachNode((AtkResNode*)root);
        }
        catch { Dispose(); throw; }
    }

    internal void Update()
    {
        if (!IsCurrent) return;
        var node = (AtkResNode*)root;
        // Title bar: reserve the rightmost 48 pixels for the native close control.
        button.Position = new Vector2(Math.Max(8, node->Width - 112), 8);
        button.IsVisible = true;
    }

    internal void StopCallbacks() { retired = true; button.OnClick = null; }
    public void Dispose() { StopCallbacks(); button.Dispose(); }
}
