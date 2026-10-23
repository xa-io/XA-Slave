using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using XASlave.Localization;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

// All saved data is pointer-free. An ornament is captured separately from the
// world player because CharaViewModelData does not include fashion accessories.
internal sealed record ViewSingleItemOrnament(ushort Id, ushort ModelCharaId)
{
    internal void Validate()
    {
        if ((Id == 0) != (ModelCharaId == 0))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SFCA5214E0E29A93C")), UiMessage.Key("SFCA5214E0E29A93C"));
    }

    internal string Hash() => Convert.ToHexString(SHA256.HashData(new byte[]
        { (byte)Id, (byte)(Id >> 8), (byte)ModelCharaId, (byte)(ModelCharaId >> 8) }));
}

// A catalog capture is one shop item, never an outfit or a fabricated player.
// Kit row zero is valid, so absence is represented by null.
internal sealed record ViewSingleItemCatalogItem(uint ItemId, int Key, uint? FramerKitId = null)
{
    internal void Validate()
    {
        if (ItemId is 0 or >= 500_000 || (!InspectOutfitPlan.Slots.Any(slot => slot == Key) && Key is not (103 or 104 or 105 or 106))
            || (Key == 105) != FramerKitId.HasValue)
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S3149D0DE91BD8A7D")), UiMessage.Key("S3149D0DE91BD8A7D"));
    }

    internal string Hash() => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"{ItemId}:{Key}:{FramerKitId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}"))));
}

// No host, actor, draw-object or texture addresses cross this boundary.
internal sealed record ViewSingleItemCapture(int Schema, string GameBuild, string Layout,
    string PlayerName, ushort World, DateTime CapturedUtc, InspectOutfitItem[] Equipment,
    ViewSingleItemChoice[] Choices, byte[] Appearance, string AppearanceHash,
    ViewSingleItemOrnament? Ornament = null, string? OrnamentHash = null,
    ViewSingleItemCompanion? Minion = null, string? MinionHash = null,
    ViewSingleItemCompanion? Mount = null, string? MountHash = null,
    ViewSingleItemCatalogItem? Catalog = null, string? CatalogHash = null)
{
    internal const string SupportedLayout = "CharaViewModelData:98:A47750CE4BE1910C8783231F398559BCCE43E4A6AE8D255351B3BBA8247D6F9D";

    internal void Validate()
    {
        if (Schema is not (1 or 2 or 3) || GameBuild != NearbyZoneNativeBinding.SupportedSha256 || Layout != SupportedLayout)
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SE828AEBEB7C17892")), UiMessage.Key("SE828AEBEB7C17892"));
        // Schema 1 has no accessory data. Schema 2 distinguishes an unavailable
        // capture (null) from a confirmed empty accessory slot (zero IDs).
        if ((Schema == 1 && (Ornament != null || OrnamentHash != null))
            || (Ornament == null && OrnamentHash != null)
            || (Ornament != null && OrnamentHash != Ornament.Hash()))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SEABBA29863DF3C4C")), UiMessage.Key("SEABBA29863DF3C4C"));
        Ornament?.Validate();
        if (Schema < 3 && (Minion != null || MinionHash != null || Mount != null || MountHash != null || Catalog != null || CatalogHash != null))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S3911D2AA5B9885B9")), UiMessage.Key("S3911D2AA5B9885B9"));
        if ((Minion == null ? MinionHash != null : Minion.Key != 103 || MinionHash != Minion.Hash())
            || (Mount == null ? MountHash != null : Mount.Key != 104 || MountHash != Mount.Hash())
            || (Catalog == null ? CatalogHash != null : CatalogHash != Catalog.Hash()))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S1A0293390ADC4095")), UiMessage.Key("S1A0293390ADC4095"));
        Minion?.Validate(); Mount?.Validate(); Catalog?.Validate();
        var expectedKeys = Catalog != null ? new[] { Catalog.Key } : Schema == 3 ? ExtendedKeys : Keys;
        if (string.IsNullOrWhiteSpace(PlayerName) || PlayerName.Length > (Catalog == null ? 64 : 512)
            || (Catalog == null ? World == 0 : World != 0)
            || CapturedUtc.Kind != DateTimeKind.Utc || Equipment == null || Equipment.Length != 12
            || Choices == null || Choices.Length != expectedKeys.Length || Choices.Any(choice => choice == null
                || string.IsNullOrEmpty(choice.Label) || choice.Label.Length > 512
                || choice.UnavailableReason == null || choice.UnavailableReason.Length > 512)
            || !Choices.Select(choice => choice.Key).Order().SequenceEqual(expectedKeys.Order())
            || Appearance == null || Appearance.Length != 0x98
            || AppearanceHash != Convert.ToHexString(SHA256.HashData(Appearance)))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SF0F322B2348FF62B")), UiMessage.Key("SF0F322B2348FF62B"));
        InspectOutfitPlan.Create(Equipment);
        if (Catalog != null)
        {
            if (Ornament != null || Choices[0].ItemId != Catalog.ItemId
                || (Catalog.Key == 103 ? Minion is not { Id: > 0 } : Minion != null)
                || (Catalog.Key == 104 ? Mount is not { Id: > 0 } : Mount != null)
                || Equipment.Any(item => item.GlamourId != 0 || item.Stain0 != 0 || item.Stain1 != 0
                    || item.BaseId != (item.Slot == Catalog.Key ? Catalog.ItemId : 0)))
                throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S9D4F18854424D54E")), UiMessage.Key("S9D4F18854424D54E"));
        }
        // Canonical value encoding excludes native padding and noncanonical bools.
        if (Appearance[0x1E] != 0 || Appearance[0x1F] != 0
            || Appearance.Skip(0x8C).Take(4).Any(value => value != 0)
            || Appearance.Skip(0x88).Take(4).Any(value => value > 1))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S4489963431C778C0")), UiMessage.Key("S4489963431C778C0"));
    }

    internal static readonly int[] Keys = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 100, 101, 102];
    internal static readonly int[] ExtendedKeys = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 100, 101, 102, 103, 104];

    internal static unsafe byte[] Encode(CharaViewModelData appearance)
    {
        if (sizeof(CharaViewModelData) != 0x98) throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SCCCBEAA91197FB2E")), UiMessage.Key("SCCCBEAA91197FB2E"));
        var bytes = new byte[0x98];
        new ReadOnlySpan<byte>(&appearance, bytes.Length).CopyTo(bytes);
        bytes[0x1E] = bytes[0x1F] = 0;
        Array.Clear(bytes, 0x8C, 4);
        return bytes;
    }

    internal unsafe CharaViewModelData Decode()
    {
        Validate();
        if (sizeof(CharaViewModelData) != 0x98) throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SCCCBEAA91197FB2E")), UiMessage.Key("SCCCBEAA91197FB2E"));
        var appearance = default(CharaViewModelData);
        Appearance.AsSpan().CopyTo(new Span<byte>(&appearance, 0x98));
        return appearance;
    }

    internal InspectOutfitSnapshot Snapshot()
        => new(default, new(0, World, PlayerName, Catalog == null ? (byte)4 : (byte)0), 0, (InspectOutfitItem[])Equipment.Clone());
}
