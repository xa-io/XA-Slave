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
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(InventoryItem).Assembly.Location))) != "2DC5B513647CC9897041D4BCFEB1E2C15EC087E6E0D04570D72F73B7614C39A5")
            throw new InvalidOperationException("Inventory shuttle needs revalidated client structures.");
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw new InvalidOperationException("Inventory shuttle needs a revalidated game build.");
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader!.SizeOfImage != module.ModuleMemorySize) throw new InvalidOperationException("Inventory image changed.");
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
                    if (match != -1) throw new InvalidOperationException("Inventory constructor signature is ambiguous.");
                    match = section.VirtualAddress + offset;
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(pattern); CryptographicOperations.ZeroMemory(mask); }
        if (match != 0x868DB9) throw new InvalidOperationException("Inventory constructor proof changed.");
        var displacement = pe.GetSectionData(match + 7).GetContent(0, 4).ToArray();
        var tableRva = checked(match + 11 + BinaryPrimitives.ReadInt32LittleEndian(displacement));
        if (tableRva != 0x2195398) throw new InvalidOperationException("Inventory vtable proof changed.");
        var entries = pe.GetSectionData(tableRva).GetContent(0, 21 * 8).ToArray();
        var imageBase = pe.PEHeaders.PEHeader.ImageBase;
        var getterRva = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(20 * 8)) - imageBase));
        var calleeRva = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(16 * 8)) - imageBase));
        if (getterRva != 0x897C00 || calleeRva != 0x8977F0) throw new InvalidOperationException("Inventory virtual function proof changed.");
        table = module.BaseAddress + tableRva; getter = module.BaseAddress + getterRva; callee = module.BaseAddress + calleeRva;
        getterProof = DecodeExact(Sigs.InventoryShuttleSlot20Sig);
        calleeProof = DecodeExact(Sigs.InventoryShuttleSlot16Sig);
        if (!pe.GetSectionData(getterRva).GetContent(0, getterProof.Length).AsSpan().SequenceEqual(getterProof)
            || !pe.GetSectionData(calleeRva).GetContent(0, calleeProof.Length).AsSpan().SequenceEqual(calleeProof))
            throw new InvalidOperationException("Inventory virtual function bytes changed.");
        Validate();
    }

    private static byte[] DecodeExact(ProtectedSig signature)
    {
        var value = new byte[signature.Length]; var mask = new byte[signature.Length];
        signature.Decode(value, mask);
        if (mask.Any(item => item != 255)) throw new InvalidOperationException("Inventory function proof is incomplete.");
        return value;
    }

    private void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        NearbyZoneNativeBinding.RequireReadable(table, 21 * 8);
        if ((nint)InventoryItem.StaticVirtualTablePointer != table || Marshal.ReadIntPtr(table, 20 * 8) != getter || Marshal.ReadIntPtr(table, 16 * 8) != callee
            || !NearbyZoneNativeBinding.Read(getter, getterProof.Length).AsSpan().SequenceEqual(getterProof)
            || !NearbyZoneNativeBinding.Read(callee, calleeProof.Length).AsSpan().SequenceEqual(calleeProof))
            throw new InvalidOperationException("Inventory live virtual function ownership changed.");
    }

    internal byte ReadSlot20(InventoryItem* slot)
    {
        Validate();
        NearbyZoneNativeBinding.RequireReadable((nint)slot, InventoryItem.StructSize);
        if (slot->IsSymbolic || (nint)slot->VirtualTable != table) throw new InvalidOperationException("Unsupported inventory virtual table.");
        return ((delegate* unmanaged<InventoryItem*, byte>)getter)(slot);
    }
}
