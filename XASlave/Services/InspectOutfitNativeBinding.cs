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
    // Inspect/Tryon layouts and helpers revalidated for the October 16 game and delivered 15.0.3.9 SDK.
    internal const string ClientStructs15037Sha256 = "A47750CE4BE1910C8783231F398559BCCE43E4A6AE8D255351B3BBA8247D6F9D";
    internal const string ClientStructs15038Sha256 = "98795452ED0636E3613850176310AEF1E4E8B85AE47A3E722FF2C325CF2801D2";
    internal const string ClientStructs15039Sha256 = "F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564";
    private readonly nint moduleBase;

    internal InspectOutfitNativeBinding()
    {
        NearbyZoneNativeBinding.RequireFramework();
        RequireSupportedClientStructs(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AgentTryon).Assembly.Location))));
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S61C6777880B2BA9C"), originalMessageText => new InvalidOperationException(originalMessageText));
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        moduleBase = module.BaseAddress;
        if (pe.PEHeaders.PEHeader?.SizeOfImage != module.ModuleMemorySize)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD3B49FB6663016D4"), originalMessageText => new InvalidOperationException(originalMessageText));
        // Validate the supported disk build and typed bindings, not unmodified
        // live function bodies: Dalamud and other plugins may legitimately hook
        // inspection and TryOn. Source/session/preview checks remain per request.
        Validate();
    }

    internal static void RequireSupportedClientStructs(string actualSha256)
    {
        if (actualSha256 != ClientStructs15037Sha256 && actualSha256 != ClientStructs15038Sha256 && actualSha256 != ClientStructs15039Sha256)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S424297BD6717DCF7" , actualSha256, ClientStructs15037Sha256, ClientStructs15038Sha256, ClientStructs15039Sha256), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if ((nint)AgentTryon.MemberFunctionPointers.TryOn != moduleBase + 0xC7C580
            || (nint)UIState.Instance() != moduleBase + 0x2AE3F90)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SA4A062DC3A1D4C38"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal bool IsPreviewActive() => Agent()->IsAgentActive();

    internal void ClearClosedPreview()
        => ClearPreview(true);

    internal void ClearPreview(bool requireClosed = false)
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE2043E40FB4C479B"), originalMessageText => new InvalidOperationException(originalMessageText));
        var agent = Agent();
        if (requireClosed && agent->IsAgentActive())
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S922EB274B2D29A69"), originalMessageText => new InvalidOperationException(originalMessageText));
        // Supported-build native clear: resets all fourteen TryOnItems. Its
        // inactive path returns without refreshing or reopening the addon.
        var clear = (delegate* unmanaged<AgentTryon*, void>)(moduleBase + 0xC7C1B0);
        clear(agent);
        var current = Agent();
        if (current != agent || (requireClosed && current->IsAgentActive()))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5875C8BA0BABFC60"), originalMessageText => new InvalidOperationException(originalMessageText));
        foreach (var item in current->TryOnItems)
            if (item.Id != 0 || item.GlamourId != 0 || item.EquipSlotCategory != 14)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S64DCC709D936E81E"), originalMessageText => new InvalidOperationException(originalMessageText));
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

    internal string TryOnOwnedFacewear(ushort glassesId, Func<bool> requestAlive) => TryOnOwnedFacewearMessage(glassesId, requestAlive).Original;

    internal global::XASlave.Localization.UiMessage TryOnOwnedFacewearMessage(ushort glassesId, Func<bool> requestAlive)
    {
        if (glassesId == 0) return global::XASlave.Localization.UiMessage.Raw(string.Empty);
        Validate();
        if (!requestAlive() || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            return global::XASlave.Localization.UiMessage.Key("S2BCFC289B5D6A8BA");
        var glasses = Plugin.DataManager.GetExcelSheet<Glasses>().GetRowOrDefault(glassesId);
        if (glasses == null) return global::XASlave.Localization.UiMessage.Key("SD44E5B72952D0A8F");
        var player = PlayerState.Instance();
        if (player == null || !player->IsGlassesUnlocked(glassesId))
            return global::XASlave.Localization.UiMessage.Key("SEACAB08A2B21082E");
        var agent = Agent();
        if (!agent->IsAgentActive() || agent->TryOnItemsChanged || agent->GearItemsChanged || !requestAlive())
            return global::XASlave.Localization.UiMessage.Key("SFAB680C8E5ADAEA3");
        var gear = CapturePreviewGear(agent);
        var original = agent->SaveDeleteOutfit;
        try
        {
            agent->SaveDeleteOutfit = true;
            // Native glasses-selection callback uses this helper. With Save
            // enabled it replaces only slot 13, preserving ordinary gear.
            var apply = (delegate* unmanaged<AgentTryon*, ushort, void>)(moduleBase + 0xC7C020);
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
        if (!requestAlive()) return global::XASlave.Localization.UiMessage.Key("S5ADD9A6214E47855");
        var current = Agent();
        if (current != agent || !gear.AsSpan().SequenceEqual(CapturePreviewGear(current)))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S581CA561E7A24B2C"), originalMessageText => new InvalidOperationException(originalMessageText));
        foreach (var item in current->TryOnItems)
            if (item.EquipSlotCategory == 13 && item.Id == glassesId)
                return global::XASlave.Localization.UiMessage.Key("S456A54E9F2A2BD03");
        return global::XASlave.Localization.UiMessage.Key("S252D8358C2D51D22");
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
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("SF9F144709679C50B" , (nint)agent, (nint)registered), originalMessageText => new InvalidOperationException(originalMessageText));
        // Vtable replacement is a normal virtual-hook mechanism. Identity comes
        // from the current agent registry, not the pristine executable vtable.
        NearbyZoneNativeBinding.RequireReadable((nint)agent->VirtualTable, sizeof(AgentTryon.AgentTryonVirtualTable));
        return agent;
    }
}
