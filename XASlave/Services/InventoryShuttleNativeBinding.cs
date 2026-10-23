using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

// The slot20 byte is compared without assigning it an inferred semantic name.
// Both its complete body and its slot16 callee are pinned; no hook is installed.
internal sealed unsafe class InventoryShuttleNativeBinding
{
    private readonly nint table, getter, callee;
    private readonly byte[] getterProof, calleeProof;

    internal InventoryShuttleNativeBinding()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(InventoryItem).Assembly.Location))) is not ("A47750CE4BE1910C8783231F398559BCCE43E4A6AE8D255351B3BBA8247D6F9D" or "98795452ED0636E3613850176310AEF1E4E8B85AE47A3E722FF2C325CF2801D2" or "F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564"))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SAFD6D93F6029C3FB"), originalMessage => new InvalidOperationException(originalMessage));
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9D81D4A2DBD2BCDE"), originalMessage => new InvalidOperationException(originalMessage));
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader!.SizeOfImage != module.ModuleMemorySize) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7CD3838FECB93AC8"), originalMessage => new InvalidOperationException(originalMessage));
        var signature = Sigs.InventoryShuttleConstructorSig;
        var pattern = new byte[signature.Length]; var mask = new byte[signature.Length];
        signature.Decode(pattern, mask);
        var match = -1;
        try
        {
            foreach (var section in pe.PEHeaders.SectionHeaders)
            {
                if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
                var code = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.VirtualSize, section.SizeOfRawData));
                for (var offset = 0; offset <= code.Length - pattern.Length; offset++)
                {
                    var equal = true;
                    for (var index = 0; index < pattern.Length; index++)
                        if (mask[index] != 0 && code[offset + index] != pattern[index]) { equal = false; break; }
                    if (!equal) continue;
                    if (match != -1) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD739D5D9D1365354"), originalMessage => new InvalidOperationException(originalMessage));
                    match = section.VirtualAddress + offset;
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(pattern); CryptographicOperations.ZeroMemory(mask); }
        if (match != 0x868DB9) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S692C6E6C4516E438"), originalMessage => new InvalidOperationException(originalMessage));
        var displacement = pe.GetSectionData(match + 7).GetContent(0, 4).ToArray();
        var tableRva = checked(match + 11 + BinaryPrimitives.ReadInt32LittleEndian(displacement));
        if (tableRva != 0x2196788) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SDA3146DC9122909A"), originalMessage => new InvalidOperationException(originalMessage));
        var entries = pe.GetSectionData(tableRva).GetContent(0, 21 * 8).ToArray();
        var imageBase = pe.PEHeaders.PEHeader.ImageBase;
        var getterRva = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(20 * 8)) - imageBase));
        var calleeRva = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(16 * 8)) - imageBase));
        if (getterRva != 0x897C00 || calleeRva != 0x8977F0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1F0588C3473398A2"), originalMessage => new InvalidOperationException(originalMessage));
        table = module.BaseAddress + tableRva; getter = module.BaseAddress + getterRva; callee = module.BaseAddress + calleeRva;
        getterProof = DecodeExact(Sigs.InventoryShuttleSlot20Sig);
        calleeProof = DecodeExact(Sigs.InventoryShuttleSlot16Sig);
        if (!pe.GetSectionData(getterRva).GetContent(0, getterProof.Length).AsSpan().SequenceEqual(getterProof)
            || !pe.GetSectionData(calleeRva).GetContent(0, calleeProof.Length).AsSpan().SequenceEqual(calleeProof))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC732D400114716F0"), originalMessage => new InvalidOperationException(originalMessage));
        Validate();
    }

    private static byte[] DecodeExact(ProtectedSig signature)
    {
        var value = new byte[signature.Length]; var mask = new byte[signature.Length];
        signature.Decode(value, mask);
        if (mask.Any(item => item != 255)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SBCF3CFDD49473DEA"), originalMessage => new InvalidOperationException(originalMessage));
        return value;
    }

    private void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        NearbyZoneNativeBinding.RequireReadable(table, 21 * 8);
        if ((nint)InventoryItem.StaticVirtualTablePointer != table || Marshal.ReadIntPtr(table, 20 * 8) != getter || Marshal.ReadIntPtr(table, 16 * 8) != callee
            || !NearbyZoneNativeBinding.Read(getter, getterProof.Length).AsSpan().SequenceEqual(getterProof)
            || !NearbyZoneNativeBinding.Read(callee, calleeProof.Length).AsSpan().SequenceEqual(calleeProof))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD33F6264A708F3F7"), originalMessage => new InvalidOperationException(originalMessage));
    }

    internal byte ReadSlot20(InventoryItem* slot)
    {
        Validate();
        NearbyZoneNativeBinding.RequireReadable((nint)slot, InventoryItem.StructSize);
        if (slot->IsSymbolic || (nint)slot->VirtualTable != table) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF28C1EA54CBDEC12"), originalMessage => new InvalidOperationException(originalMessage));
        return ((delegate* unmanaged<InventoryItem*, byte>)getter)(slot);
    }
}
