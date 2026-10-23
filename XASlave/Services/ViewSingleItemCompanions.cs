using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XASlave.Localization;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeCompanion = FFXIVClientStructs.FFXIV.Client.Game.Character.Companion;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using Companion = Lumina.Excel.Sheets.Companion;
using Mount = Lumina.Excel.Sheets.Mount;
using ModelChara = Lumina.Excel.Sheets.ModelChara;

namespace XASlave.Services;

// A sheet-backed base model, never a retained live actor or native address.
// Zero IDs confirm an empty category; null means unavailable or not captured.
internal sealed record ViewSingleItemCompanion(int Key, uint Id, uint ModelCharaId)
{
    internal void Validate()
    {
        if (Key is not (103 or 104) || Id > ushort.MaxValue || ModelCharaId > int.MaxValue
            || (Id == 0) != (ModelCharaId == 0))
            throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S1EA84B631CD853F6")), UiMessage.Key("S1EA84B631CD853F6"));
    }

    internal string Hash()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), Key);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), Id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), ModelCharaId);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

internal static class ViewSingleItemCompanions
{
    internal static ViewSingleItemCompanion? Create(int key, uint id, out string unavailable)
    {
        var result = CreateMessage(key, id, out var message);
        unavailable = message.Original;
        return result;
    }

    internal static ViewSingleItemCompanion? CreateMessage(int key, uint id, out UiMessage unavailable)
    {
        unavailable = UiMessage.Raw(string.Empty);
        try
        {
            if (key is not (103 or 104) || id > ushort.MaxValue)
                throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S19C71B4E89B8BC68")), UiMessage.Key("S19C71B4E89B8BC68"));
            if (id == 0) return new(key, 0, 0);
            uint modelId;
            if (key == 103)
            {
                var row = Plugin.DataManager.GetExcelSheet<Companion>().GetRowOrDefault(id);
                if (row == null) throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S366993F6064B7A01")), UiMessage.Key("S366993F6064B7A01"));
                modelId = row.Value.Model.RowId;
            }
            else
            {
                var row = Plugin.DataManager.GetExcelSheet<Mount>().GetRowOrDefault(id);
                if (row == null) throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S996FFB86D47346F1")), UiMessage.Key("S996FFB86D47346F1"));
                // These additional models are not represented by ModelChara
                // alone. Reject them instead of showing missing parts. The
                // separate MountCustomize sheet is rider-dependent scale and
                // camera height; this explicitly riderless base-model preview
                // uses its own scale/camera, not a captured rider appearance.
                if (!SupportsMountParts(row.Value.EquipHead, row.Value.EquipBody, row.Value.EquipLeg,
                    row.Value.EquipFoot))
                    throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S3A6E07A84FC2D327")), UiMessage.Key("S3A6E07A84FC2D327"));
                modelId = row.Value.ModelChara.RowId;
            }
            var model = Plugin.DataManager.GetExcelSheet<ModelChara>().GetRowOrDefault(modelId);
            if (modelId == 0 || modelId > int.MaxValue || model == null || model.Value.Type is not (2 or 3))
                throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("S0583FE2BFB9A42E1")), UiMessage.Key("S0583FE2BFB9A42E1"));
            return new(key, id, modelId);
        }
        catch (Exception error)
        {
            unavailable = UiMessage.FromException(error);
            return null;
        }
    }

    internal static bool SupportsMountParts(int head, int body, int legs, int feet)
        => head == 0 && body == 0 && legs == 0 && feet == 0;

    internal static ViewSingleItemChoice Choice(int key, ViewSingleItemCompanion? value, string unavailable)
        => Choice(key, value, UiMessage.Raw(unavailable));

    internal static ViewSingleItemChoice Choice(int key, ViewSingleItemCompanion? value, UiMessage unavailable)
    {
        if (key is not (103 or 104)) throw new ArgumentOutOfRangeException(nameof(key));
        var category = key == 103 ? UiMessage.Key("S2904BFF43BC4B7FE") : UiMessage.Key("SD5605821C44BA5CB");
        if (value == null) return ViewSingleItemChoice.Create(key, UiMessage.Format("S671B9AFF7E15BC44", category), 0,
            string.IsNullOrEmpty(unavailable.Original) ? (key == 103 ? UiMessage.Key("S512699EB9A526C60") : UiMessage.Key("S6A72E9198729813C")) : unavailable);
        value.Validate();
        if (value.Key != key) throw UiMessage.Attach(new InvalidDataException(UiLocale.Native("SC14387B1583DD925")), UiMessage.Key("SC14387B1583DD925"));
        if (value.Id == 0) return ViewSingleItemChoice.Create(key, key == 103 ? UiMessage.Format("S4599E7D6AEBF9AB6", category) : UiMessage.Format("S299920852414F63B", category), 0,
            key == 103 ? UiMessage.Key("S8ACFFE2E87F383A9") : UiMessage.Key("S984D74314244F4C0"));
        var name = key == 103
            ? Plugin.DataManager.GetExcelSheet<Companion>().GetRowOrDefault(value.Id)?.Singular.ToString()
            : Plugin.DataManager.GetExcelSheet<Mount>().GetRowOrDefault(value.Id)?.Singular.ToString();
        var nameMessage = string.IsNullOrWhiteSpace(name) ? UiMessage.Format("S10866A9DF5F8BE5B", category, value.Id) : UiMessage.Raw(name);
        // The sheet-backed root excludes riders and live appearance overrides.
        return ViewSingleItemChoice.Create(key, UiMessage.Format("S48E7DAAC332CACB1", category, nameMessage), 0);
    }

    internal static unsafe ViewSingleItemCompanion? Capture(InspectOutfitIdentity identity, bool mount, out string unavailable)
    {
        var result = CaptureMessage(identity, mount, out var message);
        unavailable = message.Original;
        return result;
    }

    internal static unsafe ViewSingleItemCompanion? CaptureMessage(InspectOutfitIdentity identity, bool mount, out UiMessage unavailable)
    {
        NearbyZoneNativeBinding.RequireFramework();
        unavailable = UiMessage.Raw(string.Empty);
        var key = mount ? 104 : 103;
        var category = mount ? UiMessage.Key("SD4536F2555836B0B") : UiMessage.Key("SBBEBEC50E47B8DC6");
        try
        {
            var player = ResolvePlayer(identity);
            if (player == null)
                throw CaptureError(UiMessage.Format("S5A57834A5317EFF6", category));
            var address = player.Address;
            var objectId = player.GameObjectId;
            NearbyZoneNativeBinding.RequireReadable(address, sizeof(NativeCharacter));
            var containerOffset = mount ? 0x670 : 0x6D8;
            if (Marshal.ReadIntPtr(address + containerOffset + 8) != address)
                throw CaptureError(UiMessage.Format("SF5D9905955B8A5F0", category));
            var child = Marshal.ReadIntPtr(address + containerOffset + 0x10);
            var savedId = unchecked((ushort)Marshal.ReadInt16(address + containerOffset + 0x18));
            uint id = 0;
            ViewSingleItemCompanion? result;
            if (child == 0)
            {
                // CompanionId may remain set while a mount occupies the shared
                // child slot. That is not a loaded minion and must not be saved.
                if (savedId != 0)
                    throw CaptureError(UiMessage.Format("SEB4CFC5A342998E0", category));
                result = new(key, 0, 0);
            }
            else
            {
                NearbyZoneNativeBinding.RequireReadable(child, mount ? sizeof(NativeCharacter) : sizeof(NativeCompanion));
                id = mount ? savedId : unchecked((uint)Marshal.ReadInt32(child + 0x84));
                if (id == 0)
                    throw CaptureError(UiMessage.Format("S4E141C5BA3976EEE", category));
                result = CreateMessage(key, id, out var modelFailure);
                if (result == null) throw CaptureError(modelFailure);
                if (!ChildModelReady(address, child, result))
                    throw CaptureError(UiMessage.Format("S8FEDD1478313734A", category));
            }
            var confirmed = ResolvePlayer(identity);
            if (confirmed == null || confirmed.Address != address || confirmed.GameObjectId != objectId
                || Marshal.ReadIntPtr(address + containerOffset + 8) != address
                || Marshal.ReadIntPtr(address + containerOffset + 0x10) != child
                || unchecked((ushort)Marshal.ReadInt16(address + containerOffset + 0x18)) != savedId
                || (child != 0 && (!ChildModelReady(address, child, result)
                    || (!mount && unchecked((uint)Marshal.ReadInt32(child + 0x84)) != id))))
                throw CaptureError(UiMessage.Format("SBD631D79C3F0E0E3", category));
            Plugin.Log.Information("[View Item] Companion captured: key={Key}; entity={Entity}; row={Row}; model={Model}; source=loaded-player; appearance=base-model.",
                key, identity.Entity, result.Id, result.ModelCharaId);
            return result;
        }
        catch (Exception error)
        {
            unavailable = UiMessage.Format("SAA0352ECA94AFFDE", category, UiMessage.FromException(error));
            Plugin.Log.Warning(error, "[View Item] Companion capture unavailable: key={Key}; entity={Entity}; ordinary equipment capture continues.", key, identity.Entity);
            return null;
        }
    }

    private static InvalidOperationException CaptureError(UiMessage message)
        => UiMessage.Attach(new InvalidOperationException(message.Original), message);

    private static IPlayerCharacter? ResolvePlayer(InspectOutfitIdentity identity)
    {
        if (!identity.IsPlayer) return null;
        IPlayerCharacter? match = null;
        foreach (var actor in Plugin.ObjectTable)
        {
            if (actor is not IPlayerCharacter player || player.EntityId != identity.Entity
                || player.HomeWorld.RowId != identity.World
                || !string.Equals(player.Name.ToString(), identity.Name, StringComparison.Ordinal)) continue;
            if (match != null) return null;
            match = player;
        }
        return match;
    }

    private static unsafe bool ChildModelReady(nint owner, nint child, ViewSingleItemCompanion value)
    {
        var mount = value.Key == 104;
        NearbyZoneNativeBinding.RequireReadable(owner, sizeof(NativeCharacter));
        NearbyZoneNativeBinding.RequireReadable(child, mount ? sizeof(NativeCharacter) : sizeof(NativeCompanion));
        // The native player child occupies the immediately following object
        // index. Both the owned container and Character.ChildObject must agree;
        // minions additionally expose a direct Owner pointer.
        if (Marshal.ReadIntPtr(owner + 0x22F8) != child
            || unchecked((ushort)Marshal.ReadInt16(child + 0x8C)) != unchecked((ushort)Marshal.ReadInt16(owner + 0x8C)) + 1
            || Marshal.ReadByte(child + 0x90) != (byte)(mount ? ObjectKind.Mount : ObjectKind.Companion)
            || (!mount && Marshal.ReadIntPtr(child + 0x2400) != owner)
            || Marshal.ReadIntPtr(child + 0x1B30) != child
            || Marshal.ReadInt32(child + 0x1B38) != value.ModelCharaId
            || Marshal.ReadInt32(child + 0x1B40) != -1 || Marshal.ReadInt32(child + 0x1B44) != 0)
            return false;
        var draw = (CharacterBase*)((NativeCharacter*)child)->DrawObject;
        if (draw == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
        var model = Plugin.DataManager.GetExcelSheet<ModelChara>().GetRowOrDefault(value.ModelCharaId);
        if (model == null || (byte)draw->GetModelType() != model.Value.Type
            || draw->SlotCount <= 0 || draw->SlotCount > 32 || draw->Models == null
            || (Marshal.ReadInt32((nint)draw + 0x2F0) & 0x1F) != 0
            || Marshal.ReadInt32((nint)draw + 0x920) != 0 || Marshal.ReadInt32((nint)draw + 0x3C8) != 0)
            return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
        for (var index = 0; index < draw->SlotCount; index++)
            if (draw->Models[index] != null) return true;
        return false;
    }
}
