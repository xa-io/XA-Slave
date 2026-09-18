using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

// Validated native capabilities. No hooks or writes to game code.
internal sealed unsafe class InspectOutfitNativeBinding
{
    private readonly nint moduleBase;

    internal InspectOutfitNativeBinding()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AgentTryon).Assembly.Location)))
            != "2DC5B513647CC9897041D4BCFEB1E2C15EC087E6E0D04570D72F73B7614C39A5")
            throw new InvalidOperationException("Inspection try-on requires revalidated client structures.");
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw new InvalidOperationException("Inspection try-on requires a revalidated game build.");
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        moduleBase = module.BaseAddress;
        if (pe.PEHeaders.PEHeader?.SizeOfImage != module.ModuleMemorySize)
            throw new InvalidOperationException("The inspection try-on executable image changed.");
        // Validate the supported disk build and typed bindings, not unmodified
        // live function bodies: Dalamud and other plugins may legitimately hook
        // inspection and TryOn. Source/session/preview checks remain per request.
        Validate();
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if ((nint)AgentTryon.MemberFunctionPointers.TryOn != moduleBase + 0xC7C080
            || (nint)UIState.Instance() != moduleBase + 0x2ACBF20)
            throw new InvalidOperationException("The typed inspection or try-on binding changed.");
    }

    internal bool IsPreviewActive() => Agent()->IsAgentActive();

    internal void ClearClosedPreview()
        => ClearPreview(true);

    internal void ClearPreview(bool requireClosed = false)
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            throw new InvalidOperationException("The fitting-room login session ended before clearing.");
        var agent = Agent();
        if (requireClosed && agent->IsAgentActive())
            throw new InvalidOperationException("The fitting room must be closed before clearing its previous outfit.");
        // Supported-build native clear: resets all fourteen TryOnItems. Its
        // inactive path returns without refreshing or reopening the addon.
        var clear = (delegate* unmanaged<AgentTryon*, void>)(moduleBase + 0xC7BCB0);
        clear(agent);
        var current = Agent();
        if (current != agent || (requireClosed && current->IsAgentActive()))
            throw new InvalidOperationException("The fitting room changed while clearing its previous outfit.");
        foreach (var item in current->TryOnItems)
            if (item.Id != 0 || item.GlamourId != 0 || item.EquipSlotCategory != 14)
                throw new InvalidOperationException("The previous fitting-room outfit did not clear completely.");
    }

    private static byte[] CapturePreviewGear(AgentTryon* agent)
    {
        var bytes = new List<byte>();
        for (var index = 0; index < agent->TryOnItems.Length; index++)
        {
            var item = agent->TryOnItems[index];
            if (item.Id != 0 && item.EquipSlotCategory != 13)
                bytes.AddRange(MemoryMarshal.AsBytes(agent->TryOnItems.Slice(index, 1)).ToArray());
        }
        return bytes.ToArray();
    }

    internal string TryOnOwnedFacewear(ushort glassesId, Func<bool> requestAlive)
    {
        if (glassesId == 0) return string.Empty;
        Validate();
        if (!requestAlive() || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            return "Facewear skipped because the request ended.";
        var glasses = Plugin.DataManager.GetExcelSheet<Glasses>().GetRowOrDefault(glassesId);
        if (glasses == null) return "Facewear skipped because its data is unavailable.";
        var player = PlayerState.Instance();
        if (player == null || !player->IsGlassesUnlocked(glassesId))
            return "Facewear skipped because it is not unlocked on this character.";
        var agent = Agent();
        if (!agent->IsAgentActive() || agent->TryOnItemsChanged || agent->GearItemsChanged || !requestAlive())
            return "Facewear skipped because the preview is busy.";
        var gear = CapturePreviewGear(agent);
        var original = agent->SaveDeleteOutfit;
        try
        {
            agent->SaveDeleteOutfit = true;
            // Native glasses-selection callback uses this helper. With Save
            // enabled it replaces only slot 13, preserving ordinary gear.
            var apply = (delegate* unmanaged<AgentTryon*, ushort, void>)(moduleBase + 0xC7BB20);
            apply(agent, glassesId);
        }
        finally
        {
            if (requestAlive() && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded)
            {
                var live = Agent();
                if (live == agent && live->SaveDeleteOutfit) live->SaveDeleteOutfit = original;
            }
        }
        if (!requestAlive()) return "Facewear request interrupted.";
        var current = Agent();
        if (current != agent || !gear.AsSpan().SequenceEqual(CapturePreviewGear(current)))
            throw new InvalidOperationException("Facewear submission changed the gear preview unexpectedly.");
        foreach (var item in current->TryOnItems)
            if (item.EquipSlotCategory == 13 && item.Id == glassesId)
                return "Owned facewear sent to the fitting room.";
        return "Facewear was not accepted; the gear preview was retained.";
    }

    internal AgentTryon* Agent()
    {
        Validate();
        var agent = AgentTryon.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)agent, sizeof(AgentTryon));
        var module = AgentModule.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)module, sizeof(AgentModule));
        var registered = module->GetAgentByInternalId(AgentId.Tryon);
        if ((nint)registered != (nint)agent)
            throw new InvalidOperationException($"The registered fitting-room agent changed (resolved 0x{(nint)agent:X}, registered 0x{(nint)registered:X}).");
        // Vtable replacement is a normal virtual-hook mechanism. Identity comes
        // from the current agent registry, not the pristine executable vtable.
        NearbyZoneNativeBinding.RequireReadable((nint)agent->VirtualTable, sizeof(AgentTryon.AgentTryonVirtualTable));
        return agent;
    }
}
