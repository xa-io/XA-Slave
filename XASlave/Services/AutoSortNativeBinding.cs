using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

internal sealed class AutoSortNativeBinding
{
    private readonly Dictionary<nint, byte[]> proofs = new();
    private readonly Dictionary<AutoSortFunction, nint> functions = new();
    internal nint ModuleBase { get; }
    internal nint Start { get; }
    internal nint Function(AutoSortFunction function) => functions[function];

    internal AutoSortNativeBinding()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ItemOrderModuleSorter).Assembly.Location)))
            != "2DC5B513647CC9897041D4BCFEB1E2C15EC087E6E0D04570D72F73B7614C39A5")
            throw new InvalidOperationException("Item sorting requires revalidated client structures.");
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw new InvalidOperationException("Item sorting requires a revalidated game build.");
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        ModuleBase = module.BaseAddress;
        if (pe.PEHeaders.PEHeader?.SizeOfImage != module.ModuleMemorySize)
            throw new InvalidOperationException("Item-sort executable image changed.");
        var resolved = new Dictionary<string, nint>(StringComparer.Ordinal);
        resolved.Add(Sigs.AutoSortStartCallerProof.Name, Resolve(pe, Sigs.AutoSortStartCallerProof));
        resolved.Add(Sigs.AutoSortRuleInstallerProof.Name, Resolve(pe, Sigs.AutoSortRuleInstallerProof));
        var caller = resolved["StartCaller"];
        var call = pe.GetSectionData(checked((int)(caller - ModuleBase)) + 18).GetContent(0, 5).ToArray();
        if (call[0] != 0xE8) throw new InvalidOperationException("Item-sort native start caller changed.");
        Start = caller + 23 + BinaryPrimitives.ReadInt32LittleEndian(call.AsSpan(1));
        if (Start - ModuleBase != 0x7E1700 || !proofs.ContainsKey(Start))
            throw new InvalidOperationException("Item-sort start target is not the uniquely resolved proven function.");
        var installer = resolved["RuleInstaller"];
        functions.Add(AutoSortFunction.NullSentinel, 0);
        ResolveRule(pe, installer, AutoSortFunction.IdPrimary, 0x2C, 0x7E2360);
        ResolveRule(pe, installer, AutoSortFunction.IdSecondary, 0x68, 0x7E24D0);
        ResolveRule(pe, installer, AutoSortFunction.Category, 0x38, 0x7E23A0);
        ResolveRule(pe, installer, AutoSortFunction.ItemLevel, 0x5C, 0x7E2480);
        ResolveRule(pe, installer, AutoSortFunction.Hq, 0x8C, 0x7E2590);
        ResolveRule(pe, installer, AutoSortFunction.Tab, 0x44, 0x7E2410);
        // Complete inspected key-function region, including every selected leaf body.
        Pin(pe, 0x7E2340, 0x2B0);
        RequireLayout<ItemOrderModule>(nameof(ItemOrderModule.InventorySorter), 0x48, 224);
        RequireLayout<ItemOrderModuleSorter>(nameof(ItemOrderModuleSorter.SortFunctions), 0x40, 104);
        RequireLayout<ItemOrderModuleSorter>(nameof(ItemOrderModuleSorter.SortFunctionIndex), 0x38, 104);
        RequireLayout<ItemOrderModuleSorter>(nameof(ItemOrderModuleSorter.PercentComplete), 0x3C, 104);
        RequireLayout<ItemOrderModuleSorterSortFunctionEntry>(nameof(ItemOrderModuleSorterSortFunctionEntry.FunctionPtr), 0, 16);
        RequireLayout<ItemOrderModuleSorterSortFunctionEntry>(nameof(ItemOrderModuleSorterSortFunctionEntry.Descending), 8, 16);
        Validate();
    }

    private static void RequireLayout<T>(string member, int offset, int size) where T : struct
    {
        if (Marshal.OffsetOf<T>(member).ToInt32() != offset || Marshal.SizeOf<T>() != size)
            throw new InvalidOperationException("Item-sort typed layout changed: " + member);
    }

    private nint Resolve(PEReader pe, FurnitureNativeProof definition)
    {
        var bytes = new byte[definition.Signature.Length]; var mask = new byte[bytes.Length];
        definition.Signature.Decode(bytes, mask);
        var match = -1;
        try
        {
            foreach (var section in pe.PEHeaders.SectionHeaders)
            {
                if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
                var content = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.VirtualSize, section.SizeOfRawData));
                for (var offset = 0; offset <= content.Length - bytes.Length; offset++)
                {
                    var equal = true;
                    for (var index = 0; index < bytes.Length; index++)
                        if (mask[index] != 0 && bytes[index] != content[offset + index]) { equal = false; break; }
                    if (!equal) continue;
                    if (match >= 0) throw new InvalidOperationException("Ambiguous item-sort signature: " + definition.Name);
                    match = section.VirtualAddress + offset;
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(mask); }
        if (match != definition.Rva) throw new InvalidOperationException("Item-sort signature mismatch: " + definition.Name);
        var directory = pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var records = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        foreach (var (begin, end, unwind) in definition.Fragments)
        {
            var found = false;
            for (var index = 0; index + 12 <= records.Length; index += 12)
                if (BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index)) == begin
                    && BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index + 4)) == end
                    && BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index + 8)) == unwind) { found = true; break; }
            if (!found || end <= begin || end - begin > 65536) throw new InvalidOperationException("Item-sort proven native extent changed.");
            Pin(pe, begin, end - begin);
        }
        return ModuleBase + match;
    }

    private void ResolveRule(PEReader pe, nint installer, AutoSortFunction function, int offset, int expected)
    {
        var at = installer + offset;
        var instruction = pe.GetSectionData(checked((int)(at - ModuleBase))).GetContent(0, 7).ToArray();
        var target = at + 7 + BinaryPrimitives.ReadInt32LittleEndian(instruction.AsSpan(3));
        if (!instruction.AsSpan(0, 3).SequenceEqual(new byte[] { 0x48, 0x8D, 0x05 }) || target - ModuleBase != expected)
            throw new InvalidOperationException("Item-sort rule function mapping changed.");
        functions.Add(function, target);
    }

    private void Pin(PEReader pe, int begin, int length)
    {
        if (!pe.PEHeaders.SectionHeaders.Any(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0
            && begin >= section.VirtualAddress && (long)begin + length <= (long)section.VirtualAddress + Math.Min(section.VirtualSize, section.SizeOfRawData)))
            throw new InvalidOperationException("Item-sort proof is outside executable code.");
        proofs.Add(ModuleBase + begin, pe.GetSectionData(begin).GetContent(0, length).ToArray());
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        // These addresses identify expected rule entries; XA never calls them.
        // The on-disk build/signature/layout checks above establish that mapping.
        // Live function bodies can legitimately contain Dalamud or plugin hooks.
        // Command read-back and the transaction validate the state we consume.
        if (Plugin.SigScanner.Module.BaseAddress != ModuleBase)
            throw new InvalidOperationException("The item-sort module changed.");
    }
}
