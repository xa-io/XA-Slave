using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>Exact supported executable/layout binding. Diagnostic RVAs are never accepted for another build.</summary>
internal sealed unsafe class NearbyZoneNativeBinding
{
    internal const string SupportedSha256 = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    private const string NovemberGameSha256 = "CEB4260B2574476ACF72E7B6105D0A4F8D1BD78B4E7CAB5D8115C453F2F39339";
    private const string NovemberClientStructsSha256 = "2D268F54FB6C75B511C349BA49B00E8D197C09FF64FF380ACABDCAE42A1F7D9B";
    private const string NovemberStableClientStructsSha256 = "28488F18AD05FDCD12373A83756EAB275C291B09C6AF061EF7F846AA06A36E29";
    internal readonly nint ModuleBase;
    internal readonly int ImageSize;
    private readonly bool november;
    private readonly Dictionary<int, byte[]> proofs = new();
    private readonly List<(int Start, int End)> executable = new();
    private readonly UTF8Encoding strictUtf8 = new(false, true);
    // The shared October identity still protects other features and saved captures.
    // Only this binding selects the separately reviewed November game/SDK pair.
    private int SearchEndRva => november ? 0x96EB80 : 0x96EB00;
    private int SearchRequestRva => november ? 0x96EBF0 : 0x96EB70;
    private int ContentEndRva => november ? 0x985280 : 0x9851D0;
    private int ContentStartRva => november ? 0x985320 : 0x985270;
    private int ContentReceiveEventRva => november ? 0x58F1D0 : 0x58F480;
    private int InfoProxyGetterRva => november ? 0x958670 : 0x958600;
    private int AgentGetterRva => november ? 0x494100 : 0x4943B0;
    private int SearchTableRva => november ? 0x21A1938 : 0x219F498;
    private int ContentTableRva => november ? 0x21A2338 : 0x219FE98;
    private int ContentRequestRva => november ? 0x99F210 : 0x99F3F0;
    private int ContentAgentTableRva => november ? 0x2177688 : 0x2176158;
    internal nint SearchEnd => ModuleBase + SearchEndRva;
    internal nint SearchRequest => ModuleBase + SearchRequestRva;
    internal nint ContentEnd => ModuleBase + ContentEndRva;
    internal nint ContentStart => ModuleBase + ContentStartRva;

    internal static bool IsSupportedPair(string gameHash, string clientStructsHash)
        => gameHash == SupportedSha256
            || (gameHash == NovemberGameSha256 && clientStructsHash is (NovemberClientStructsSha256 or NovemberStableClientStructsSha256));

    internal NearbyZoneNativeBinding()
    {
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        var gameHash = Convert.ToHexString(SHA256.HashData(bytes));
        // Preserve the existing October admission; the new profile has an exact SDK pin.
        var clientStructsHash = gameHash == SupportedSha256 ? string.Empty
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(InfoProxySearch).Assembly.Location)));
        if (!IsSupportedPair(gameHash, clientStructsHash)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S96715DD91DA36CFF"), originalMessageText => new InvalidOperationException(originalMessageText));
        november = gameHash == NovemberGameSha256;
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        ModuleBase = module.BaseAddress;
        ImageSize = pe.PEHeaders.PEHeader?.SizeOfImage ?? 0;
        if (ImageSize == 0 || module.ModuleMemorySize != ImageSize) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S24E7BDF27A77F344"), originalMessageText => new InvalidOperationException(originalMessageText));
        foreach (var section in pe.PEHeaders.SectionHeaders)
            if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)
                executable.Add((section.VirtualAddress, checked(section.VirtualAddress + section.VirtualSize)));
        // Exact extents verified against the supported PE exception table (including chained fragments), or the bounded leaf body.
        AddProof(pe, SearchEndRva, 0x65); AddProof(pe, SearchRequestRva, 0x144);
        AddProof(pe, ContentEndRva, 0x8B); AddProof(pe, ContentStartRva, 0x17);
        AddProof(pe, ContentReceiveEventRva, 0x2F8);
        AddProof(pe, InfoProxyGetterRva, 11); AddProof(pe, AgentGetterRva, 8);
        if (november)
        {
            AddProof(pe, 0xD2910, 0x10); // Framework.GetUIModule, including the null branch.
            AddProof(pe, 0x788050, 8); AddProof(pe, 0x788080, 8); // UIModule info/agent getters.
            AddProof(pe, 0x482530, 8); // Both agents' IsAgentActive.
        }
        if (sizeof(InfoProxySearch) != 400 || sizeof(InfoProxyCommonList) != 208 || sizeof(InfoProxyCommonList.CharacterData) != 112
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.DataSize)).ToInt32() != 0xA2
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.CharData)).ToInt32() != 0xB0
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.EntryCount)).ToInt32() != 0x10
            || Marshal.OffsetOf<InfoProxyCommonList>(nameof(InfoProxyCommonList.CurrentRequestId)).ToInt32() != 0x19)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (november) ValidateNovemberLayouts();
    }

    private static void ValidateNovemberLayouts()
    {
        if (sizeof(InfoProxyContentMember) != 0xD58 || sizeof(AgentInterface) != 0x28 || sizeof(AtkValue) != 0x10
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.LocationCount), 0xD0)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.JobMask), 0xD8)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.LevelMin), 0xE0)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.LevelMax), 0xE2)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.GrandCompanyMask), 0xE8)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.LanguageMask), 0xF0)
            || !HasExpectedFieldOffset<InfoProxySearch>(nameof(InfoProxySearch.OnlineStatusMask), 0xF8)
            || !HasExpectedFieldOffset<InfoProxySearch>("_locationIDs", 0x100)
            || !HasExpectedFieldOffset<InfoProxySearch>("_name", 0x164)
            || !HasExpectedFieldOffset<InfoProxyCommonList.CharacterData>(nameof(InfoProxyCommonList.CharacterData.HomeWorld), 0x28)
            || !HasExpectedFieldOffset<InfoProxyCommonList.CharacterData>(nameof(InfoProxyCommonList.CharacterData.Location), 0x2A)
            || !HasExpectedFieldOffset<InfoProxyCommonList.CharacterData>(nameof(InfoProxyCommonList.CharacterData.Job), 0x31)
            || !HasExpectedFieldOffset<InfoProxyCommonList.CharacterData>("_name", 0x32)
            || !HasExpectedFieldOffset<AgentInterface>(nameof(AgentInterface.AddonId), 0x20)
            || !HasExpectedFieldOffset<AtkValue>(nameof(AtkValue.Type), 0)
            || !HasExpectedFieldOffset<AtkValue>(nameof(AtkValue.Int), 8))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    private static bool HasExpectedFieldOffset<T>(string field, int expected) where T : struct
    {
        // Explicit ClientStructs layouts can contain fields the interop marshaler
        // cannot represent. Inspect the declared field offset without marshaling T.
        var type = typeof(T);
        var info = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (type.StructLayoutAttribute?.Value != LayoutKind.Explicit || info == null) return false;
        var offset = info.GetCustomAttribute<FieldOffsetAttribute>();
        return offset != null && offset.Value == expected;
    }

    private void ValidateNovemberModuleRoutes()
    {
        if (!november) return;
        if ((nint)Framework.StaticAddressPointers.ppInstance != ModuleBase + 0x2904928
            || (nint)Framework.MemberFunctionPointers.GetUIModule != ModuleBase + 0xD2910)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
        RequireReadable(ModuleBase + 0x2904928, 8);
        ValidateUnownedTarget(ModuleBase + 0xD2910);
        var framework = Framework.Instance();
        RequireReadable((nint)framework, 0x2B70);
        var ui = framework->GetUIModule();
        RequireReadable((nint)ui, 8);
        var table = Marshal.ReadIntPtr((nint)ui);
        if (table != ModuleBase + 0x2190778)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
        RequireReadable(table, 38 * 8);
        if (Marshal.ReadIntPtr(table, 35 * 8) != ModuleBase + 0x788050
            || Marshal.ReadIntPtr(table, 37 * 8) != ModuleBase + 0x788080)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
        ValidateUnownedTarget(ModuleBase + 0x788050);
        ValidateUnownedTarget(ModuleBase + 0x788080);
    }

    private void AddProof(PEReader pe, int rva, int length)
    {
        if (!executable.Any(s => rva >= s.Start && (long)rva + length <= s.End)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S68666262063D21A9"), originalMessageText => new InvalidOperationException(originalMessageText));
        proofs.Add(rva, pe.GetSectionData(rva).GetContent(0, length).ToArray());
    }

    internal byte[] ValidateUnownedTarget(nint target)
    {
        var rva = checked((int)(target - ModuleBase));
        if (!proofs.TryGetValue(rva, out var proof) || !Read(target, proof.Length).AsSpan().SequenceEqual(proof))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S8ADBAABBA5B05D83" , rva), originalMessageText => new InvalidOperationException(originalMessageText));
        return proof;
    }

    internal void ValidateInstalledTarget(nint target, byte[] installedPrefix)
    {
        var proof = proofs[checked((int)(target - ModuleBase))];
        var actual = Read(target, proof.Length);
        if (!actual.AsSpan(0, installedPrefix.Length).SequenceEqual(installedPrefix)
            || !actual.AsSpan(installedPrefix.Length).SequenceEqual(proof.AsSpan(installedPrefix.Length)))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB671F3701AC5B696"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal InfoProxyCommonList* CurrentProxy(NearbyZoneMode mode)
    {
        RequireFramework();
        ValidateNovemberModuleRoutes();
        if ((nint)InfoModule.MemberFunctionPointers.GetInfoProxyById != ModuleBase + InfoProxyGetterRva) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SBF7AF0FBFFD92046"), originalMessageText => new InvalidOperationException(originalMessageText));
        ValidateUnownedTarget(ModuleBase + InfoProxyGetterRva);
        var pointer = mode == NearbyZoneMode.Search ? (InfoProxyCommonList*)InfoProxySearch.Instance() : (InfoProxyCommonList*)InfoProxyContentMember.Instance();
        ValidateProxy(pointer, mode);
        return pointer;
    }

    internal void ValidateProxy(InfoProxyCommonList* pointer, NearbyZoneMode mode)
    {
        RequireReadable((nint)pointer, mode == NearbyZoneMode.Search ? 400 : 208);
        var expected = ModuleBase + (mode == NearbyZoneMode.Search ? SearchTableRva : ContentTableRva);
        if (Marshal.ReadIntPtr((nint)pointer) != expected) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7816CDFE0484C96B"), originalMessageText => new InvalidOperationException(originalMessageText));
        RequireReadable(expected, 88);
        var request = Marshal.ReadIntPtr(expected, 24);
        var end = Marshal.ReadIntPtr(expected, 80);
        if (request != (mode == NearbyZoneMode.Search ? SearchRequest : ModuleBase + ContentRequestRva)
            || end != (mode == NearbyZoneMode.Search ? SearchEnd : ContentEnd)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEA5C9D0CB1EBA749"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (pointer->DataSize != 200 || pointer->EntryCount > 200 || pointer->CharData == null)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S3AD52C22A334713F" , mode, pointer->DataSize, pointer->EntryCount, ((pointer->CharData == null ? global::XASlave.Localization.UiMessage.Key("SFFA63583DFA6706B") : global::XASlave.Localization.UiMessage.Key("S4D4C7EEE2E28D03C")))), originalMessageText => new InvalidOperationException(originalMessageText));
        RequireReadable((nint)pointer->CharData, 200 * 112);
    }

    internal AgentInterface* Agent(NearbyZoneMode mode)
    {
        RequireFramework();
        ValidateNovemberModuleRoutes();
        if ((nint)AgentModule.MemberFunctionPointers.GetAgentByInternalId != ModuleBase + AgentGetterRva) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S504210DD49E7A769"), originalMessageText => new InvalidOperationException(originalMessageText));
        ValidateUnownedTarget(ModuleBase + AgentGetterRva);
        var module = AgentModule.Instance();
        if (module == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4310051CF210BA30"), originalMessageText => new InvalidOperationException(originalMessageText));
        var agent = module->GetAgentByInternalId((AgentId)(mode == NearbyZoneMode.Search ? 63 : 299));
        RequireReadable((nint)agent, november ? 0x28 : 8);
        var table = Marshal.ReadIntPtr((nint)agent);
        RequireReadable(table, 0x38);
        if (november)
        {
            if (table != ModuleBase + (mode == NearbyZoneMode.Search ? 0x2174818 : ContentAgentTableRva)
                || Marshal.ReadIntPtr(table, 0x30) != ModuleBase + 0x482530)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCBC5EE83B1B68B21"), originalMessageText => new InvalidOperationException(originalMessageText));
            ValidateUnownedTarget(ModuleBase + 0x482530);
        }
        if (mode == NearbyZoneMode.ContentMember)
        {
            var receiveEvent = Marshal.ReadIntPtr(table);
            if (table != ModuleBase + ContentAgentTableRva || receiveEvent != ModuleBase + ContentReceiveEventRva)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S8391DA809DD9CA6B" , global::XASlave.Localization.UiMessage.Format("SF02CBA92BAB94B6D" , (long)(table - ModuleBase)), global::XASlave.Localization.UiMessage.Format("S195CFBC75E452443" , (long)(receiveEvent - ModuleBase))), originalMessageText => new InvalidOperationException(
                    originalMessageText));
            ValidateUnownedTarget(ModuleBase + ContentReceiveEventRva);
        }
        return agent;
    }

    internal NearbyZoneObservation Copy(InfoProxyCommonList* pointer, NearbyZoneMode mode, long generation, uint territory)
    {
        RequireFramework();
        if (pointer != CurrentProxy(mode)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF35A57FF0E9A8E24"), originalMessageText => new InvalidOperationException(originalMessageText));
        ValidateProxy(pointer, mode);
        var buffer = pointer->CharData;
        var count = pointer->EntryCount;
        var result = new List<NearbyZonePlayer>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            ref var row = ref buffer[i];
            var bytes = row.Name;
            var end = bytes.IndexOf((byte)0);
            if (end < 0 || end > 31) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7EF568290B964C8C"), originalMessageText => new InvalidOperationException(originalMessageText));
            var name = strictUtf8.GetString(bytes[..end]);
            if (mode == NearbyZoneMode.Search && row.Location != territory) continue;
            result.Add(new(name, row.HomeWorld, row.Job, row.Location));
        }
        if (pointer != CurrentProxy(mode) || pointer->CharData != buffer || pointer->EntryCount != count)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S87FFA87755B462A5"), originalMessageText => new InvalidOperationException(originalMessageText));
        return new(mode, generation, territory, DateTime.UtcNow, checked((int)count), result.AsReadOnly());
    }

    internal static byte[] Read(nint address, int length)
    {
        RequireReadable(address, length);
        var bytes = new byte[length]; Marshal.Copy(address, bytes, 0, length); return bytes;
    }

    internal static void RequireFramework()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD4C23EAC29C4D519"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal static void RequireReadable(nint address, int length)
        => RequireAccess(address, length, false);

    internal static void RequireWritable(nint address, int length)
        => RequireAccess(address, length, true);

    private static void RequireAccess(nint address, int length, bool writable)
    {
        if (address == 0 || length <= 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S50C3DDDFB203049D"), originalMessageText => new InvalidOperationException(originalMessageText));
        var end = checked((nuint)address + (nuint)length);
        for (var cursor = (nuint)address; cursor < end;)
        {
            if (VirtualQuery((nint)cursor, out var page, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0 || page.State != 0x1000
                || (page.Protect & 0x101) != 0 || (page.Protect & (writable ? 0xCCu : 0xEEu)) == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3471A8CE7673E0A2"), originalMessageText => new InvalidOperationException(originalMessageText));
            var next = checked((nuint)page.BaseAddress + page.RegionSize);
            if (next <= cursor) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S20324B22E53BB653"), originalMessageText => new InvalidOperationException(originalMessageText));
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
