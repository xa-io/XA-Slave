using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>Exact supported executable/layout binding. Diagnostic RVAs are never accepted for another build.</summary>
internal sealed unsafe class NearbyZoneNativeBinding
{
    internal const string SupportedSha256 = "5BBC501DD5C7F22FD61A11D08C25356041D878DB7CD83203ADAE393E4DFACC44";
    internal readonly nint ModuleBase;
    internal readonly int ImageSize;
    private readonly Dictionary<int, byte[]> proofs = new();
    private readonly List<(int Start, int End)> executable = new();
    private readonly UTF8Encoding strictUtf8 = new(false, true);
    internal nint SearchEnd => ModuleBase + 0x96EB00;
    internal nint SearchRequest => ModuleBase + 0x96EB70;
    internal nint ContentEnd => ModuleBase + 0x985190;
    internal nint ContentStart => ModuleBase + 0x985230;

    internal NearbyZoneNativeBinding()
    {
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != SupportedSha256) throw new InvalidOperationException("Zone cache requires a revalidated game build.");
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        ModuleBase = module.BaseAddress;
        ImageSize = pe.PEHeaders.PEHeader?.SizeOfImage ?? 0;
        if (ImageSize == 0 || module.ModuleMemorySize != ImageSize) throw new InvalidOperationException("Zone cache image identity changed.");
        foreach (var section in pe.PEHeaders.SectionHeaders)
            if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)
                executable.Add((section.VirtualAddress, checked(section.VirtualAddress + section.VirtualSize)));
        // Exact extents verified against the supported PE exception table, or the bounded leaf body.
        AddProof(pe, 0x96EB00, 0x65); AddProof(pe, 0x96EB70, 0x144);
        AddProof(pe, 0x985190, 0x8B); AddProof(pe, 0x985230, 0x17);
        AddProof(pe, 0x58F230, 0x8B);
        AddProof(pe, 0x958600, 11); AddProof(pe, 0x494160, 8);
        if (sizeof(InfoProxySearch) != 400 || sizeof(InfoProxyCommonList) != 208 || sizeof(InfoProxyCommonList.CharacterData) != 112
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.DataSize)).ToInt32() != 0xA2
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.CharData)).ToInt32() != 0xB0
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.EntryCount)).ToInt32() != 0x10
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.CurrentRequestId)).ToInt32() != 0x19)
            throw new InvalidOperationException("Zone proxy layout changed.");
    }

    private void AddProof(PEReader pe, int rva, int length)
    {
        if (!executable.Any(s => rva >= s.Start && (long)rva + length <= s.End)) throw new InvalidOperationException("Zone target is outside executable code.");
        proofs.Add(rva, pe.GetSectionData(rva).GetContent(0, length).ToArray());
    }

    internal byte[] ValidateUnownedTarget(nint target)
    {
        var rva = checked((int)(target - ModuleBase));
        if (!proofs.TryGetValue(rva, out var proof) || !Read(target, proof.Length).AsSpan().SequenceEqual(proof))
            throw new InvalidOperationException($"Zone target at RVA 0x{rva:X} differs from the supported game code or is already hooked.");
        return proof;
    }

    internal void ValidateInstalledTarget(nint target, byte[] installedPrefix)
    {
        var proof = proofs[checked((int)(target - ModuleBase))];
        var actual = Read(target, proof.Length);
        if (!actual.AsSpan(0, installedPrefix.Length).SequenceEqual(installedPrefix)
            || !actual.AsSpan(installedPrefix.Length).SequenceEqual(proof.AsSpan(installedPrefix.Length)))
            throw new InvalidOperationException("Zone hook or surrounding code changed.");
    }

    internal InfoProxyCommonList* CurrentProxy(NearbyZoneMode mode)
    {
        RequireFramework();
        if ((nint)InfoModule.MemberFunctionPointers.GetInfoProxyById != ModuleBase + 0x958600) throw new InvalidOperationException("Typed proxy lookup changed.");
        ValidateUnownedTarget(ModuleBase + 0x958600);
        var pointer = mode == NearbyZoneMode.Search ? (InfoProxyCommonList*)InfoProxySearch.Instance() : (InfoProxyCommonList*)InfoProxyContentMember.Instance();
        ValidateProxy(pointer, mode);
        return pointer;
    }

    internal void ValidateProxy(InfoProxyCommonList* pointer, NearbyZoneMode mode)
    {
        RequireReadable((nint)pointer, mode == NearbyZoneMode.Search ? 400 : 208);
        var expected = ModuleBase + (mode == NearbyZoneMode.Search ? 0x219DF58 : 0x219E958);
        if (Marshal.ReadIntPtr((nint)pointer) != expected) throw new InvalidOperationException("Zone proxy has an unexpected vtable.");
        RequireReadable(expected, 88);
        var request = Marshal.ReadIntPtr(expected, 24);
        var end = Marshal.ReadIntPtr(expected, 80);
        if (request != (mode == NearbyZoneMode.Search ? SearchRequest : ModuleBase + 0x99F130)
            || end != (mode == NearbyZoneMode.Search ? SearchEnd : ContentEnd)) throw new InvalidOperationException("Zone proxy virtual methods changed.");
        if (pointer->DataSize != 200 || pointer->EntryCount > 200 || pointer->CharData == null)
            throw new InvalidOperationException($"{mode} cache validation failed: capacity={pointer->DataSize} (expected 200), count={pointer->EntryCount} (maximum 200), buffer={(pointer->CharData == null ? "missing" : "present")}.");
        RequireReadable((nint)pointer->CharData, 200 * 112);
    }

    internal AgentInterface* Agent(NearbyZoneMode mode)
    {
        RequireFramework();
        if ((nint)AgentModule.MemberFunctionPointers.GetAgentByInternalId != ModuleBase + 0x494160) throw new InvalidOperationException("Typed agent lookup changed.");
        ValidateUnownedTarget(ModuleBase + 0x494160);
        var module = AgentModule.Instance();
        if (module == null) throw new InvalidOperationException("Agent module is unavailable.");
        var agent = module->GetAgentByInternalId((AgentId)(mode == NearbyZoneMode.Search ? 63 : 299));
        RequireReadable((nint)agent, 8);
        var table = Marshal.ReadIntPtr((nint)agent);
        RequireReadable(table, 0x30);
        if (mode == NearbyZoneMode.ContentMember)
        {
            var receiveEvent = Marshal.ReadIntPtr(table);
            if (table != ModuleBase + 0x2175448 || receiveEvent != ModuleBase + 0x58F230)
                throw new InvalidOperationException(
                    $"Content-member event binding changed: agent=299, vtable offset=0x{(long)(table - ModuleBase):X} (expected 0x2175448), "
                    + $"event offset=0x{(long)(receiveEvent - ModuleBase):X} (expected 0x58F230). Content refresh was not sent.");
            ValidateUnownedTarget(ModuleBase + 0x58F230);
        }
        return agent;
    }

    internal NearbyZoneObservation Copy(InfoProxyCommonList* pointer, NearbyZoneMode mode, long generation, uint territory)
    {
        RequireFramework();
        if (pointer != CurrentProxy(mode)) throw new InvalidOperationException("Zone proxy identity changed before capture.");
        ValidateProxy(pointer, mode);
        var buffer = pointer->CharData;
        var count = pointer->EntryCount;
        var result = new List<NearbyZonePlayer>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            ref var row = ref buffer[i];
            var bytes = row.Name;
            var end = bytes.IndexOf((byte)0);
            if (end < 0 || end > 31) throw new InvalidOperationException("Zone cache contains an unterminated player name.");
            var name = strictUtf8.GetString(bytes[..end]);
            if (mode == NearbyZoneMode.Search && row.Location != territory) continue;
            result.Add(new(name, row.HomeWorld, row.Job, row.Location));
        }
        if (pointer != CurrentProxy(mode) || pointer->CharData != buffer || pointer->EntryCount != count)
            throw new InvalidOperationException("Zone cache changed during capture.");
        return new(mode, generation, territory, DateTime.UtcNow, checked((int)count), result.AsReadOnly());
    }

    internal static byte[] Read(nint address, int length)
    {
        RequireReadable(address, length);
        var bytes = new byte[length]; Marshal.Copy(address, bytes, 0, length); return bytes;
    }

    internal static void RequireFramework()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Zone cache access requires the framework thread.");
    }

    internal static void RequireReadable(nint address, int length)
        => RequireAccess(address, length, false);

    internal static void RequireWritable(nint address, int length)
        => RequireAccess(address, length, true);

    private static void RequireAccess(nint address, int length, bool writable)
    {
        if (address == 0 || length <= 0) throw new InvalidOperationException("Invalid zone memory range.");
        var end = checked((nuint)address + (nuint)length);
        for (var cursor = (nuint)address; cursor < end;)
        {
            if (VirtualQuery((nint)cursor, out var page, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0 || page.State != 0x1000
                || (page.Protect & 0x101) != 0 || (page.Protect & (writable ? 0xCCu : 0xEEu)) == 0) throw new InvalidOperationException("Zone memory range has incompatible access.");
            var next = checked((nuint)page.BaseAddress + page.RegionSize);
            if (next <= cursor) throw new InvalidOperationException("Invalid zone memory region.");
            cursor = next;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo
    {
        internal nint BaseAddress, AllocationBase;
        internal uint AllocationProtect, Alignment;
        internal nuint RegionSize;
        internal uint State, Protect, Type, Padding;
    }
    [DllImport("kernel32.dll")] private static extern nuint VirtualQuery(nint address, out MemoryInfo information, nuint size);
}
