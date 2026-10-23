using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

internal sealed unsafe class InspectOutfitWorld : IInspectOutfitSource
{
    private readonly Action requireSupported;
    private readonly Func<long> frameworkFrame;
    private global::XASlave.Localization.UiMessage? unavailableMessage;
    internal global::XASlave.Localization.UiMessage UnavailableMessage => global::XASlave.Localization.UiMessage.Capture(unavailableMessage, UnavailableReason);
    internal string UnavailableReason { get; private set; } = string.Empty;
    internal InspectOutfitWorld(Action requireSupported, Func<long> frameworkFrame)
    { this.requireSupported = requireSupported; this.frameworkFrame = frameworkFrame; }

    private bool Ready()
    {
        NearbyZoneNativeBinding.RequireFramework();
        requireSupported();
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("S0C81FF267D7CDA75")); return false; }
        return true;
    }

    internal InspectOutfitIdentity? Identity()
    {
        if (!Ready()) return null;
        var ui = UIState.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)ui, sizeof(UIState));
        var inspect = &ui->Inspect;
        if (inspect->IsInspectRequested)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("S20648B1420E8EFDF")); return null; }
        var identity = new InspectOutfitIdentity(inspect->EntityId, inspect->WorldId, inspect->NameString, inspect->Type);
        if (!identity.IsPlayer)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("SF11A73C207ABEB72")); return null; }
        UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        return identity;
    }

    internal ushort Facewear(InspectOutfitSnapshot source)
    {
        var current = Capture(source.Host, source.Identity);
        if (current == null || !source.SameSource(current)) return 0;
        return UIState.Instance()->Inspect.GlassesIds[0];
    }

    public InspectOutfitSnapshot? Capture(InspectOutfitHost host, InspectOutfitIdentity identity)
    {
        if (!Ready()) return null;
        if (InspectOutfitControls.Resolve(host.Generation) != host || Identity() != identity)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("SFE861E2EC2E3AB5D")); return null; }
        var agent = AgentInspect.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)agent, sizeof(AgentInspect));
        if (!agent->IsAgentActive() || agent->AddonId != host.Id || agent->IsBuddyInspect)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("SB912F7E8443F3BA2")); return null; }
        var manager = InventoryManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(InventoryManager));
        var container = manager->GetInventoryContainer(InventoryType.Examine);
        NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
        var slots = container->Items; var size = container->Size;
        if (!container->IsLoaded || container->Type != InventoryType.Examine || slots == null || size < 14 || size > 64)
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("SBC68E58B8B2CCE72")); return null; }
        NearbyZoneNativeBinding.RequireReadable((nint)slots, checked(size * sizeof(InventoryItem)));
        var source = new InspectOutfitItem[InspectOutfitPlan.Slots.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var slot = InspectOutfitPlan.Slots[index]; var item = slots + slot;
            if (item->IsSymbolic || item->Container != InventoryType.Examine || item->Slot != slot
                || item->VirtualTable != InventoryItem.StaticVirtualTablePointer)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9643DF596C5E16A3"), originalMessageText => new InvalidOperationException(originalMessageText));
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
        { UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Key("SF18558A11D6E0FD2")); return null; }
        UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        return new(host, identity, frameworkFrame(), source);
    }
}
