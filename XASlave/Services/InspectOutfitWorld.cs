using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

internal sealed unsafe class InspectOutfitWorld
{
    private readonly Action requireSupported;
    private readonly Func<long> frameworkFrame;
    internal string UnavailableReason { get; private set; } = string.Empty;
    internal InspectOutfitWorld(Action requireSupported, Func<long> frameworkFrame)
    { this.requireSupported = requireSupported; this.frameworkFrame = frameworkFrame; }

    private bool Ready()
    {
        NearbyZoneNativeBinding.RequireFramework();
        requireSupported();
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
        { UnavailableReason = "The local player is not loaded."; return false; }
        return true;
    }

    internal InspectOutfitIdentity? Identity()
    {
        if (!Ready()) return null;
        var ui = UIState.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)ui, sizeof(UIState));
        var inspect = &ui->Inspect;
        if (inspect->IsInspectRequested)
        { UnavailableReason = "The game is still fetching the inspection."; return null; }
        var identity = new InspectOutfitIdentity(inspect->EntityId, inspect->WorldId, inspect->NameString, inspect->Type);
        if (!identity.IsPlayer)
        { UnavailableReason = "The inspection has no complete player identity."; return null; }
        UnavailableReason = string.Empty;
        return identity;
    }

    internal ushort Facewear(InspectOutfitSnapshot source)
    {
        var current = Capture(source.Host, source.Identity);
        if (current == null || !source.SameSource(current)) return 0;
        return UIState.Instance()->Inspect.GlassesIds[0];
    }

    internal InspectOutfitSnapshot? Capture(InspectOutfitHost host, InspectOutfitIdentity identity)
    {
        if (!Ready()) return null;
        if (InspectOutfitControls.Resolve(host.Generation) != host || Identity() != identity)
        { UnavailableReason = "The inspection window or player identity changed."; return null; }
        var agent = AgentInspect.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)agent, sizeof(AgentInspect));
        if (!agent->IsAgentActive() || agent->AddonId != host.Id || agent->IsBuddyInspect)
        { UnavailableReason = "The player inspection agent is not ready for this window."; return null; }
        var manager = InventoryManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(InventoryManager));
        var container = manager->GetInventoryContainer(InventoryType.Examine);
        NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
        var slots = container->Items; var size = container->Size;
        if (!container->IsLoaded || container->Type != InventoryType.Examine || slots == null || size < 14 || size > 64)
        { UnavailableReason = "The Examine equipment container is not ready."; return null; }
        NearbyZoneNativeBinding.RequireReadable((nint)slots, checked(size * sizeof(InventoryItem)));
        var source = new InspectOutfitItem[InspectOutfitPlan.Slots.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var slot = InspectOutfitPlan.Slots[index]; var item = slots + slot;
            if (item->IsSymbolic || item->Container != InventoryType.Examine || item->Slot != slot
                || item->VirtualTable != InventoryItem.StaticVirtualTablePointer)
                throw new InvalidOperationException("An Examine inventory slot has an incompatible identity.");
            var descriptor = new InspectOutfitItem(slot, item->ItemId, item->GlamourId, item->Stains[0], item->Stains[1]);
            // Examine is the equipment source. Agent display caches and request
            // bookkeeping need not match it and are not readiness conditions.
            source[index] = descriptor;
        }
        if (!Ready() || Identity() != identity || InspectOutfitControls.Resolve(host.Generation) != host
            || agent != AgentInspect.Instance() || !agent->IsAgentActive() || agent->AddonId != host.Id
            || agent->IsBuddyInspect || manager != InventoryManager.Instance()
            || container != manager->GetInventoryContainer(InventoryType.Examine) || !container->IsLoaded
            || container->Items != slots || container->Size != size)
        { UnavailableReason = "The inspected equipment changed while it was being read."; return null; }
        UnavailableReason = string.Empty;
        return new(host, identity, frameworkFrame(), source);
    }
}
