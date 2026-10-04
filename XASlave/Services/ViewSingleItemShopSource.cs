using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Item = Lumina.Excel.Sheets.Item;

namespace XASlave.Services;

internal sealed record ViewSingleItemCatalogEntry(uint ItemId, int Key, string Name,
    uint? FramerKitId = null, ViewSingleItemCompanion? Companion = null);

// Catalog classification and sparse appearance construction shared by all item
// sources. ViewSingleItemContextSource resolves context-menu identities.
internal static unsafe class ViewSingleItemShopSource
{
    internal static uint NormalizeItemId(uint raw)
        => (raw is > 0 and < 2_000_000) ? raw % 500_000 : 0;

    internal static bool TryDescribe(uint itemId, [NotNullWhen(true)] out ViewSingleItemCatalogEntry? entry, out string reason)
    {
        entry = null;
        reason = string.Empty;
        try
        {
            if (itemId == 0 || itemId != NormalizeItemId(itemId))
                throw new InvalidDataException("The shop item identifier is invalid.");
            var optional = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
            if (optional == null) throw new InvalidDataException("The shop item is absent from the current game data.");
            var item = optional.Value;
            var name = item.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) name = "Item " + itemId;
            var action = item.ItemAction.ValueNullable;
            var actionId = action?.Action.RowId ?? 0;
            if (actionId is 853 or 1322)
            {
                var key = actionId == 853 ? 103 : 104;
                var id = (uint)action!.Value.Data[0];
                if (id == 0) throw new InvalidDataException("The shop item has no minion or mount identifier.");
                var companion = ViewSingleItemCompanions.Create(key, id, out reason);
                if (companion == null) return false;
                entry = new(itemId, key, name, Companion: companion);
                return true;
            }
            if (item.ItemAction.RowId == 2234)
            {
                // Kit zero is valid; null, not zero, means no framer's kit.
                entry = new(itemId, 105, name, FramerKitId: item.AdditionalData.RowId);
                return true;
            }
            if (ViewSingleItemFurnitureAssets.TryDescribe(itemId, out _))
            {
                entry = new(itemId, 106, name);
                return true;
            }
            var category = item.EquipSlotCategory.ValueNullable;
            if (category == null) throw new InvalidDataException("This shop item has no supported visual preview.");
            var slots = category.Value;
            var keyForGear = GearKey([slots.MainHand, slots.OffHand, slots.Head, slots.Body, slots.Gloves,
                slots.Waist, slots.Legs, slots.Feet, slots.Ears, slots.Neck, slots.Wrists,
                slots.FingerR, slots.FingerL, slots.SoulCrystal]);
            if (keyForGear < 0 || item.ModelMain == 0)
                throw new InvalidDataException("This shop item has no supported equipment model.");
            if (!SupportsWeaponParts(keyForGear, item.ModelMain, item.ModelSub))
                throw new InvalidDataException("This fist weapon needs a separate gauntlet model that shop previews do not support yet.");
            entry = new(itemId, keyForGear, name);
            return true;
        }
        catch (Exception error)
        {
            reason = error.Message;
            return false;
        }
    }

    internal static int GearKey(sbyte[] slots)
    {
        if (slots.Length != 14) return -1;
        for (var index = 0; index < slots.Length; index++)
            if (slots[index] > 0) return index is 5 or 13 ? -1 : index;
        return -1;
    }

    internal static bool SupportsWeaponParts(int key, ulong main, ulong sub)
    {
        var id = (ushort)main;
        return key != 0 || sub == 0 || sub >= 0x1_0000_0000
            || !((id is > 300 and <= 350) || (id is > 1600 and <= 1650));
    }

    internal static ViewSingleItemCapture CreateCapture(ViewSingleItemCatalogEntry entry, byte[] referenceAppearance)
    {
        var appearance = CreateAppearance(entry, referenceAppearance);
        var catalog = new ViewSingleItemCatalogItem(entry.ItemId, entry.Key, entry.FramerKitId);
        var equipment = InspectOutfitPlan.Slots.Select(slot => new InspectOutfitItem(slot,
            slot == entry.Key ? entry.ItemId : 0, 0, 0, 0)).ToArray();
        var minion = entry.Key == 103 ? entry.Companion : null;
        var mount = entry.Key == 104 ? entry.Companion : null;
        var capture = new ViewSingleItemCapture(3, NearbyZoneNativeBinding.SupportedSha256,
            ViewSingleItemCapture.SupportedLayout, entry.Name, 0, DateTime.UtcNow,
            equipment, [new(entry.Key, entry.Name, entry.ItemId)], appearance,
            Convert.ToHexString(SHA256.HashData(appearance)), Minion: minion, MinionHash: minion?.Hash(),
            Mount: mount, MountHash: mount?.Hash(), Catalog: catalog, CatalogHash: catalog.Hash());
        capture.Validate();
        return capture;
    }

    internal static byte[] CreateAppearance(ViewSingleItemCatalogEntry entry, byte[] referenceAppearance)
    {
        if (referenceAppearance.Length != 0x98)
            throw new InvalidDataException("The reference appearance has an unsupported format.");
        if (!TryDescribe(entry.ItemId, out var current, out var reason) || current != entry)
            throw new InvalidDataException("The shop item changed or is unavailable. " + reason);
        var appearance = default(CharaViewModelData);
        // Keep the character's race/body customization, never their equipment,
        // dyes, hidden-ears state, glasses, crest or another captured selection.
        referenceAppearance.AsSpan(0, 0x1A).CopyTo(new Span<byte>(&appearance, 0x1A));
        if (entry.Key >= 103) return ViewSingleItemCapture.Encode(appearance);
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(entry.ItemId);
        if (entry.Key is 0 or 1)
        {
            appearance.WeaponModelIds[entry.Key] = Weapon(item.ModelMain);
            if (entry.Key == 0 && item.ModelSub != 0) appearance.WeaponModelIds[1] = Weapon(item.ModelSub);
        }
        else
        {
            var slot = ModelSlot(entry.Key);
            if (slot < 0) throw new InvalidDataException("The selected equipment slot is unsupported.");
            appearance.EquipmentModelIds[slot] = new EquipmentModelId
            { Id = (ushort)item.ModelMain, Variant = (byte)(item.ModelMain >> 16) };
        }
        return ViewSingleItemCapture.Encode(appearance);
    }

    private static WeaponModelId Weapon(ulong model) => new()
    { Id = (ushort)model, Type = (ushort)(model >> 16), Variant = (ushort)(model >> 32) };

    internal static int ModelSlot(int key) => key switch
    {
        2 => 0, 3 => 1, 4 => 2, 6 => 3, 7 => 4, 8 => 5, 9 => 6,
        10 => 7, 11 => 8, 12 => 9, _ => -1,
    };
}
