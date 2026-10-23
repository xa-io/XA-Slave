using System;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Data;

namespace XASlave.Services;

internal static unsafe class NearbyZoneRequestAdapter
{
    internal static bool ScreenReady()
    {
        NearbyZoneNativeBinding.RequireFramework();
        return !BlockingAddon("NowLoading") && !BlockingAddon("FadeMiddle") && !BlockingAddon("FadeBack");
    }

    private static bool BlockingAddon(string name)
    {
        var addon = AddonHelper.GetAddon(name);
        return addon != null && addon->IsVisible && addon->IsFullyLoaded();
    }

    internal static void Request(NearbyZoneNativeBinding binding, NearbyZoneMode mode, long generation, Func<long> currentGeneration, ushort place)
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (!ScreenReady()) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5259C0E5737B6EAE"), originalMessageText => new InvalidOperationException(originalMessageText));
        var proxy = binding.CurrentProxy(mode);
        var agent = binding.Agent(mode);
        if (agent->IsAgentActive()) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCB021F7F02DBE431"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (currentGeneration() != generation) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SDC6462AA597F11F2"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (mode == NearbyZoneMode.ContentMember)
        {
            AtkValue returned = default;
            AtkValue value = default;
            value.Type = AtkValueType.Int;
            value.Int = 1;
            agent->ReceiveEvent(&returned, &value, 1, 0);
            return; // The proxy's RequestData is a false-returning stub and must never be used here.
        }
        RequestSearch(binding, (InfoProxySearch*)proxy, generation, currentGeneration, place);
    }

    private static void RequestSearch(NearbyZoneNativeBinding binding, InfoProxySearch* proxy, long generation, Func<long> currentGeneration, ushort place)
    {
        if (place == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S134D4361DDE9595E"), originalMessageText => new InvalidOperationException(originalMessageText));
        NearbyZoneNativeBinding.RequireWritable((nint)proxy, 400);
        // Save precisely the fields owned by this request. Arrays are restored as whole fields.
        var jobs = proxy->JobMask;
        var min = proxy->LevelMin; var max = proxy->LevelMax;
        var company = proxy->GrandCompanyMask; var language = proxy->LanguageMask;
        var online = proxy->OnlineStatusMask;
        var count = proxy->LocationCount;
        var locations = proxy->LocationIDs.ToArray();
        var name = proxy->Name.ToArray();
        if (locations.Length != 50 || name.Length != 32) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE5781CE0D03F60E0"), originalMessageText => new InvalidOperationException(originalMessageText));
        Span<ushort> writtenLocations = stackalloc ushort[50]; writtenLocations.Clear(); writtenLocations[0] = place;
        Span<byte> writtenName = stackalloc byte[32]; writtenName.Clear();
        var wantedOnline = (ulong)InfoProxyCommonList.CharacterData.OnlineStatus.Online;
        try
        {
            proxy->JobMask = ulong.MaxValue;
            proxy->LevelMin = 1; proxy->LevelMax = 255;
            proxy->GrandCompanyMask = byte.MaxValue; proxy->LanguageMask = byte.MaxValue;
            proxy->OnlineStatusMask = wantedOnline;
            writtenLocations.CopyTo(proxy->LocationIDs); proxy->LocationCount = 1;
            writtenName.CopyTo(proxy->Name);
            _ = proxy->RequestData(); // The outer native sender overwrites transport failure with true.
        }
        finally
        {
            // Native RequestData snapshots the filters synchronously. Never restore through a stale pointer,
            // across a generation, or over a field changed by reentrant user UI activity.
            if (currentGeneration() == generation && (nint)binding.CurrentProxy(NearbyZoneMode.Search) == (nint)proxy)
            {
                NearbyZoneNativeBinding.RequireWritable((nint)proxy, 400);
                if (proxy->JobMask == ulong.MaxValue) proxy->JobMask = jobs;
                if (proxy->LevelMin == 1) proxy->LevelMin = min;
                if (proxy->LevelMax == 255) proxy->LevelMax = max;
                if (proxy->GrandCompanyMask == byte.MaxValue) proxy->GrandCompanyMask = company;
                if (proxy->LanguageMask == byte.MaxValue) proxy->LanguageMask = language;
                if (proxy->OnlineStatusMask == wantedOnline) proxy->OnlineStatusMask = online;
                if (proxy->LocationCount == 1 && proxy->LocationIDs.SequenceEqual(writtenLocations))
                {
                    locations.AsSpan().CopyTo(proxy->LocationIDs); proxy->LocationCount = count;
                }
                if (proxy->Name.SequenceEqual(writtenName)) name.AsSpan().CopyTo(proxy->Name);
            }
        }
    }
}
