using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            throw new InvalidDataException("The saved minion or mount is incomplete or invalid.");
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
        unavailable = string.Empty;
        try
        {
            if (key is not (103 or 104) || id > ushort.MaxValue)
                throw new InvalidDataException("The minion or mount identifier is invalid.");
            if (id == 0) return new(key, 0, 0);
            uint modelId;
            if (key == 103)
            {
                var row = Plugin.DataManager.GetExcelSheet<Companion>().GetRowOrDefault(id);
                if (row == null) throw new InvalidDataException("The minion is absent from the current game data.");
                modelId = row.Value.Model.RowId;
            }
            else
            {
                var row = Plugin.DataManager.GetExcelSheet<Mount>().GetRowOrDefault(id);
                if (row == null) throw new InvalidDataException("The mount is absent from the current game data.");
                // These additional models are not represented by ModelChara
                // alone. Reject them instead of showing missing parts. The
                // separate MountCustomize sheet is rider-dependent scale and
                // camera height; this explicitly riderless base-model preview
                // uses its own scale/camera, not a captured rider appearance.
                if (!SupportsMountParts(row.Value.EquipHead, row.Value.EquipBody, row.Value.EquipLeg,
                    row.Value.EquipFoot))
                    throw new InvalidDataException("This mount needs additional equipment that the standalone preview does not support yet.");
                modelId = row.Value.ModelChara.RowId;
            }
            var model = Plugin.DataManager.GetExcelSheet<ModelChara>().GetRowOrDefault(modelId);
            if (modelId == 0 || modelId > int.MaxValue || model == null || model.Value.Type is not (2 or 3))
                throw new InvalidDataException("This minion or mount has no supported standalone model.");
            return new(key, id, modelId);
        }
        catch (Exception error)
        {
            unavailable = error.Message;
            return null;
        }
    }

    internal static bool SupportsMountParts(int head, int body, int legs, int feet)
        => head == 0 && body == 0 && legs == 0 && feet == 0;

    internal static ViewSingleItemChoice Choice(int key, ViewSingleItemCompanion? value, string unavailable)
    {
        if (key is not (103 or 104)) throw new ArgumentOutOfRangeException(nameof(key));
        var category = key == 103 ? "Minion" : "Mount";
        if (value == null) return new(key, category + ": not captured", 0,
            string.IsNullOrEmpty(unavailable) ? "No " + category.ToLowerInvariant() + " was captured." : unavailable);
        value.Validate();
        if (value.Key != key) throw new InvalidDataException("The captured model belongs to another category.");
        if (value.Id == 0) return new(key, category + (key == 103 ? ": not summoned" : ": not in use"), 0,
            key == 103 ? "The player had no visible minion when captured." : "The player was not riding a mount when captured.");
        var name = key == 103
            ? Plugin.DataManager.GetExcelSheet<Companion>().GetRowOrDefault(value.Id)?.Singular.ToString()
            : Plugin.DataManager.GetExcelSheet<Mount>().GetRowOrDefault(value.Id)?.Singular.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = category + " " + value.Id;
        // The sheet-backed root excludes riders and live appearance overrides.
        return new(key, category + ": " + name + " (base model)", 0);
    }

    internal static unsafe ViewSingleItemCompanion? Capture(InspectOutfitIdentity identity, bool mount, out string unavailable)
    {
        NearbyZoneNativeBinding.RequireFramework();
        unavailable = string.Empty;
        var key = mount ? 104 : 103;
        var category = mount ? "mount" : "minion";
        try
        {
            var player = ResolvePlayer(identity);
            if (player == null)
                throw new InvalidOperationException("Capturing a " + category + " requires the inspected player to be nearby and loaded.");
            var address = player.Address;
            var objectId = player.GameObjectId;
            NearbyZoneNativeBinding.RequireReadable(address, sizeof(NativeCharacter));
            var containerOffset = mount ? 0x670 : 0x6D8;
            if (Marshal.ReadIntPtr(address + containerOffset + 8) != address)
                throw new InvalidOperationException("The player's " + category + " container ownership changed.");
            var child = Marshal.ReadIntPtr(address + containerOffset + 0x10);
            var savedId = unchecked((ushort)Marshal.ReadInt16(address + containerOffset + 0x18));
            uint id = 0;
            ViewSingleItemCompanion? result;
            if (child == 0)
            {
                // CompanionId may remain set while a mount occupies the shared
                // child slot. That is not a loaded minion and must not be saved.
                if (savedId != 0)
                    throw new InvalidOperationException("The player's " + category + " is not currently loaded or visible.");
                result = new(key, 0, 0);
            }
            else
            {
                NearbyZoneNativeBinding.RequireReadable(child, mount ? sizeof(NativeCharacter) : sizeof(NativeCompanion));
                id = mount ? savedId : unchecked((uint)Marshal.ReadInt32(child + 0x84));
                if (id == 0)
                    throw new InvalidOperationException("The player's " + category + " is still loading.");
                result = Create(key, id, out var modelFailure);
                if (result == null) throw new InvalidOperationException(modelFailure);
                if (!ChildModelReady(address, child, result))
                    throw new InvalidOperationException("The player's " + category + " ownership or model is still loading or changing.");
            }
            var confirmed = ResolvePlayer(identity);
            if (confirmed == null || confirmed.Address != address || confirmed.GameObjectId != objectId
                || Marshal.ReadIntPtr(address + containerOffset + 8) != address
                || Marshal.ReadIntPtr(address + containerOffset + 0x10) != child
                || unchecked((ushort)Marshal.ReadInt16(address + containerOffset + 0x18)) != savedId
                || (child != 0 && (!ChildModelReady(address, child, result)
                    || (!mount && unchecked((uint)Marshal.ReadInt32(child + 0x84)) != id))))
                throw new InvalidOperationException("The player or " + category + " changed during capture.");
            Plugin.Log.Information("[View Item] Companion captured: key={Key}; entity={Entity}; row={Row}; model={Model}; source=loaded-player; appearance=base-model.",
                key, identity.Entity, result.Id, result.ModelCharaId);
            return result;
        }
        catch (Exception error)
        {
            unavailable = "The " + category + " is unavailable: " + error.Message;
            Plugin.Log.Warning(error, "[View Item] Companion capture unavailable: key={Key}; entity={Entity}; ordinary equipment capture continues.", key, identity.Entity);
            return null;
        }
    }

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
