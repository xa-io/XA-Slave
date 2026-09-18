using System;
using System.Linq;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

internal sealed record InspectOutfitNativeState(InspectOutfitPreview Preview, byte[] RawRecords,
    bool SaveDeleteOutfit, bool DisplayGear, InventoryType Inventory, uint Opener, nint Addon, uint AddonId)
{
    internal bool SameAdmission(InspectOutfitNativeState other) => Preview.SameSession(other.Preview)
        && Preview.Changed == other.Preview.Changed && Preview.Transitioning == other.Preview.Transitioning
        && SaveDeleteOutfit == other.SaveDeleteOutfit && DisplayGear == other.DisplayGear && Inventory == other.Inventory
        && Opener == other.Opener && Addon == other.Addon && AddonId == other.AddonId
        && RawRecords.AsSpan().SequenceEqual(other.RawRecords);
}

// Session and request generations are managed by the owning framework service.
// A logout invalidates session immediately, including reentrant logout in TryOn.
internal sealed unsafe class InspectOutfitNativeAccess
{
    private readonly InspectOutfitNativeBinding binding;
    private readonly InspectOutfitWorld world;
    private readonly Func<long> session, frame;
    private readonly Func<long, bool> requestAlive;

    internal InspectOutfitNativeAccess(InspectOutfitNativeBinding binding, InspectOutfitWorld world,
        Func<long> session, Func<long> frame, Func<long, bool> requestAlive)
    { this.binding = binding; this.world = world; this.session = session; this.frame = frame; this.requestAlive = requestAlive; }

    internal InspectOutfitNativeState Capture()
    {
        var epoch = session();
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            throw new InvalidOperationException("The fitting-room login session ended.");
        var agent = binding.Agent();
        var records = agent->TryOnItems;
        if (records.Length != 14) throw new InvalidOperationException("The fitting-room record layout changed.");
        var raw = MemoryMarshal.AsBytes(records).ToArray();
        var descriptions = new InspectPreviewRecord[14];
        for (var index = 0; index < descriptions.Length; index++)
        {
            var item = records[index];
            descriptions[index] = new(item.Id, item.GlamourId, item.Stain0Id, item.Stain1Id,
                item.GlamourStain0Id, item.GlamourStain1Id, item.EquipSlotCategory, item.GlamourEquipSlotCategory, item.ApplyCompanyCrest);
        }
        var active = agent->IsAgentActive();
        var addon = AddonHelper.GetAddon("Tryon");
        var ready = active && addon != null && addon->Id == agent->AddonId && addon->IsFullyLoaded()
            && addon->IsVisible && addon->RootNode != null && agent->CharaView.CharacterLoaded;
        // Inactive agents do not run the preview update loop, so display flags
        // can remain set until the first TryOn opens the room. Pending tuples
        // still count as work; never discard another request's queued items.
        var changed = agent->TryOnItemsChanged && (active || InspectOutfitPlan.HasPending(descriptions));
        // DoUpdate is a render-refresh request, not item-queue ownership.
        // Clear sets it at 0xC7BF1A; AgentTryon.Update sets it again at
        // 0xC7B4CD. Waiting for it to be false can deadlock an open room.
        var transitioning = active && (agent->GearItemsChanged || !ready);
        var preview = new InspectOutfitPreview((nint)agent, epoch, frame(), changed, transitioning, ready, descriptions);
        var result = new InspectOutfitNativeState(preview, raw, agent->SaveDeleteOutfit, agent->DisplayGear,
            agent->EquippedItemsInventoryType, agent->OpenerAddonId, (nint)addon, addon == null ? 0u : checked((uint)addon->Id));
        var live = binding.Agent();
        if (epoch != session() || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || agent != live
            || !raw.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(agent->TryOnItems)))
            throw new InvalidOperationException("The fitting-room snapshot changed during capture.");
        return result;
    }

    internal (bool Accepted, InspectOutfitNativeState After) Submit(InspectOutfitSnapshot source, InspectOutfitItem item,
        InspectOutfitNativeState before, long generation)
    {
        var current = world.Capture(source.Host, source.Identity);
        if (!requestAlive(generation) || current == null || !source.SameSource(current))
            throw new InvalidOperationException("The inspected outfit request changed before submission.");
        var fresh = Capture();
        if (!before.SameAdmission(fresh) || fresh.Preview.Changed || fresh.Preview.Transitioning
            || InspectOutfitPlan.HasPending(fresh.Preview.Records) || !InspectOutfitPlan.HasCapacity(fresh.Preview.Records))
            throw new InvalidOperationException("The fitting room changed before submission.");
        var agent = binding.Agent();
        var epoch = session();
        if (!requestAlive(generation) || (nint)agent != fresh.Preview.Agent || epoch != fresh.Preview.Session)
            throw new InvalidOperationException("The fitting-room request ended before submission.");
        var original = agent->SaveDeleteOutfit;
        bool accepted;
        try
        {
            agent->SaveDeleteOutfit = true;
            accepted = AgentTryon.TryOn(0, item.Appearance.Item, item.Stain0, item.Stain1, 0, false);
        }
        finally
        {
            // No deferred pointer restoration. Session and request must still
            // belong to this synchronous call before even resolving the agent.
            if (epoch == session() && requestAlive(generation) && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded)
            {
                var live = binding.Agent();
                if (epoch == session() && requestAlive(generation) && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded
                    && live == agent && live->SaveDeleteOutfit) live->SaveDeleteOutfit = original;
            }
        }
        if (!requestAlive(generation) || epoch != session())
            throw new InvalidOperationException("The outfit request ended during submission.");
        return (accepted, Capture());
    }
}
