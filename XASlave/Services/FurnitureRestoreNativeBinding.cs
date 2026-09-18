using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

// Resolved addresses precede diagnostic RVA checks. No alternate-client/RVA fallback.
// Construction validates disk/live bodies and layouts but issues no housing action.
internal sealed unsafe class FurnitureRestoreNativeBinding
{
    // Only Prepare touches this field, on the framework thread. The worker reads
    // files/metadata only. Each batch receives separate observer ownership state.
    private static Task<FurnitureRestoreNativeBinding>? preparation;
    // Reloaded disables its reverse wrapper, not the game's entry jump. Retain
    // only prefixes previously installed by this plugin and successfully disabled.
    // Framework-thread access only; this is never populated from arbitrary bytes.
    private static readonly Dictionary<nint, byte[]> retired = GetRetiredProofs();
    private static readonly Dictionary<nint, Dictionary<nint, byte[]>> retiredPaths = GetRetiredPaths();

    private static Dictionary<nint, Dictionary<nint, byte[]>> GetRetiredPaths()
    {
        var domain = AppDomain.CurrentDomain;
        var key = "XASlave.FurnitureRestore.RetiredPaths.v2." + NearbyZoneNativeBinding.SupportedSha256;
        lock (domain)
        {
            var saved = domain.GetData(key);
            if (saved is Dictionary<nint, Dictionary<nint, byte[]>> existing) return existing;
            if (saved != null) throw new InvalidOperationException("Furniture inactive observer path registry has an incompatible format.");
            var created = new Dictionary<nint, Dictionary<nint, byte[]>>();
            domain.SetData(key, created);
            return created;
        }
    }

    private static Dictionary<nint, byte[]> GetRetiredProofs()
    {
        // Plugin statics disappear on reload; Reloaded's inactive native entries
        // survive. Store only BCL values so this does not root a plugin assembly,
        // delegate, service or game object. This state dies with the game process.
        var domain = AppDomain.CurrentDomain;
        var key = "XASlave.FurnitureRestore.RetiredPrefixes.v1." + NearbyZoneNativeBinding.SupportedSha256;
        lock (domain)
        {
            var saved = domain.GetData(key);
            if (saved is Dictionary<nint, byte[]> existing) return existing;
            if (saved != null) throw new InvalidOperationException("Furniture inactive observer registry has an incompatible format.");
            var created = new Dictionary<nint, byte[]>();
            domain.SetData(key, created);
            return created;
        }
    }
    private readonly Dictionary<string, nint> functions = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, byte[]> proofs = new();
    private readonly Dictionary<nint, byte[]> installed = new();
    private readonly Dictionary<nint, Dictionary<nint, byte[]>> forwarding = new();
    private readonly Dictionary<nint, Dictionary<nint, byte[]>> installedPaths = new();
    private readonly Dictionary<nint, Dictionary<nint, byte[]>> originals = new();
    private readonly List<(nint Address, nint Expected)> tables = [];
    internal nint ModuleBase { get; }
    internal nint AgentTable { get; }
    internal nint IndoorListenerTable { get; }
    internal nint OutdoorListenerTable { get; }
    internal nint Function(string name) => functions[name];

    internal static void Prepare()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (preparation != null) return;
        var module = Plugin.SigScanner.Module;
        var fileName = module.FileName;
        var moduleBase = module.BaseAddress;
        var moduleSize = module.ModuleMemorySize;
        var sdkPath = typeof(HousingManager).Assembly.Location;
        preparation = Task.Run(() => new FurnitureRestoreNativeBinding(fileName, moduleBase, moduleSize, sdkPath));
    }

    internal static FurnitureRestoreNativeBinding CreatePrepared()
    {
        Prepare();
        // Never block Draw/Update on disk hashing or signature scanning.
        if (!preparation!.IsCompleted)
            throw new InvalidOperationException("Furniture native checks are preparing in the background. Try again shortly.");
        var source = preparation.GetAwaiter().GetResult();
        var result = new FurnitureRestoreNativeBinding(source);
        result.Validate();
        return result;
    }

    private FurnitureRestoreNativeBinding(FurnitureRestoreNativeBinding source)
    {
        ModuleBase = source.ModuleBase; AgentTable = source.AgentTable;
        IndoorListenerTable = source.IndoorListenerTable; OutdoorListenerTable = source.OutdoorListenerTable;
        foreach (var entry in source.functions) functions.Add(entry.Key, entry.Value);
        foreach (var entry in source.proofs) proofs.Add(entry.Key, entry.Value);
        tables.AddRange(source.tables);
    }

    private FurnitureRestoreNativeBinding(string fileName, nint moduleBase, int moduleSize, string sdkPath)
    {
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdkPath)))
            != "2DC5B513647CC9897041D4BCFEB1E2C15EC087E6E0D04570D72F73B7614C39A5")
            throw new InvalidOperationException("Furniture restoration requires revalidated client structures.");
        var bytes = File.ReadAllBytes(fileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw new InvalidOperationException("Furniture restoration requires a revalidated game build.");
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        ModuleBase = moduleBase;
        if (pe.PEHeaders.PEHeader?.SizeOfImage != moduleSize)
            throw new InvalidOperationException("Furniture executable image identity changed.");
        Resolve(pe, Sigs.FurnitureRestoreInventoryRestoreProof);
        Resolve(pe, Sigs.FurnitureRestoreStorageRestoreProof);
        Resolve(pe, Sigs.FurnitureRestoreIndoorInventoryProof);
        Resolve(pe, Sigs.FurnitureRestoreOutdoorInventoryProof);
        Resolve(pe, Sigs.FurnitureRestoreIndoorStorageProof);
        Resolve(pe, Sigs.FurnitureRestoreIndoorDecisionProof);
        Resolve(pe, Sigs.FurnitureRestoreOutdoorDecisionProof);
        Resolve(pe, Sigs.FurnitureRestoreWarningOpenProof);
        Resolve(pe, Sigs.FurnitureRestoreIndoorPlacedSizeProof);
        Resolve(pe, Sigs.FurnitureRestoreOutdoorSizeProof);
        Resolve(pe, Sigs.FurnitureRestoreIndoorStoredSizeProof);
        Resolve(pe, Sigs.FurnitureRestoreCurrentHouseProof);
        Resolve(pe, Sigs.FurnitureRestoreAgentConstructorProof);
        Resolve(pe, Sigs.FurnitureRestoreCountProducerProof);

        RequireBranch(pe, Function("InventoryRestore") + 12, 1, 5, Function("OutdoorInventory"));
        RequireBranch(pe, Function("InventoryRestore") + 24, 2, 6, Function("IndoorInventory"));
        RequireBranch(pe, Function("StorageRestore") + 0x99, 1, 5, Function("IndoorStorage"));
        var constructor = Function("AgentConstructor");
        var lea = pe.GetSectionData(checked((int)(constructor - ModuleBase)) + 0x2B).GetContent(0, 7).ToArray();
        if (!lea.AsSpan(0, 3).SequenceEqual(new byte[] { 0x48, 0x8D, 0x05 }))
            throw new InvalidOperationException("Housing agent constructor layout changed.");
        AgentTable = constructor + 0x32 + BinaryPrimitives.ReadInt32LittleEndian(lea.AsSpan(3));
        if (AgentTable - ModuleBase != 0x22CE708) throw new InvalidOperationException("Housing agent vtable changed.");
        var updater = ResolveTableEntry(pe, AgentTable, 7);
        if (updater - ModuleBase != 0x1720A80) throw new InvalidOperationException("Housing agent updater changed.");
        Pin(pe, checked((int)(updater - ModuleBase)), 0x33);
        RequireBranch(pe, updater + 0x2E, 1, 5, Function("CountProducer"));
        IndoorListenerTable = FindReceiverTable(pe, Function("IndoorDecision"), 0x21CECA0);
        OutdoorListenerTable = FindReceiverTable(pe, Function("OutdoorDecision"), 0x21CEC38);

        RequireOffset<HousingManager>(nameof(HousingManager.CurrentTerritory), 0);
        RequireOffset<HousingManager>(nameof(HousingManager.OutdoorTerritory), 8);
        RequireOffset<HousingManager>(nameof(HousingManager.IndoorTerritory), 16);
        RequireOffset<IndoorTerritory>(nameof(IndoorTerritory.HouseId), 0x125D0);
        RequireOffset<OutdoorTerritory>(nameof(OutdoorTerritory.HouseId), 0x125D0);
        RequireOffset<OutdoorTerritory>(nameof(OutdoorTerritory.HouseUnit), 0x125D8);
        RequireOffset<IndoorTerritory.IndoorTerritoryUIEventListener>(nameof(IndoorTerritory.IndoorTerritoryUIEventListener.InventoryItem), 24);
        RequireOffset<IndoorTerritory.IndoorTerritoryUIEventListener>(nameof(IndoorTerritory.IndoorTerritoryUIEventListener.AddonId), 32);
        RequireOffset<AgentInterface>(nameof(AgentInterface.AddonId), 32);
        if ((int)AgentId.Housing != 120 || sizeof(HousingManager) != 232 || sizeof(InventoryItem) != 72)
            throw new InvalidOperationException("Furniture native size/identity metadata changed.");
    }

    private static void RequireOffset<T>(string field, int expected) where T : struct
    {
        // ClientStructs uses explicit CLR layouts, including nested types that the
        // interop marshaler cannot represent. Read that layout metadata directly;
        // never ask Marshal.OffsetOf to marshal the entire housing territory.
        var type = typeof(T);
        var offset = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetCustomAttribute<FieldOffsetAttribute>();
        if (type.StructLayoutAttribute?.Value != LayoutKind.Explicit || offset == null || offset.Value != expected)
            throw new InvalidOperationException("Furniture native field layout changed: " + type.Name + "." + field);
    }

    private void Resolve(PEReader pe, FurnitureNativeProof definition)
    {
        var pattern = new byte[definition.Signature.Length]; var mask = new byte[pattern.Length];
        definition.Signature.Decode(pattern, mask);
        var match = -1;
        try
        {
            foreach (var section in pe.PEHeaders.SectionHeaders)
            {
                if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
                var data = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.VirtualSize, section.SizeOfRawData));
                for (var offset = 0; offset <= data.Length - pattern.Length; offset++)
                {
                    var equal = true;
                    for (var index = 0; index < pattern.Length; index++)
                        if (mask[index] != 0 && pattern[index] != data[offset + index]) { equal = false; break; }
                    if (!equal) continue;
                    if (match >= 0) throw new InvalidOperationException("Ambiguous furniture signature: " + definition.Name);
                    match = section.VirtualAddress + offset;
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(pattern); CryptographicOperations.ZeroMemory(mask); }
        if (match != definition.Rva) throw new InvalidOperationException("Furniture signature mismatch: " + definition.Name);
        functions.Add(definition.Name, ModuleBase + match);
        var directory = pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var records = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        foreach (var (begin, end, unwind) in definition.Fragments)
        {
            var found = false; var overlapping = false;
            for (var index = 0; index + 12 <= records.Length; index += 12)
            {
                var otherBegin = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index));
                var otherEnd = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index + 4));
                if (otherBegin < end && otherEnd > begin) overlapping = true;
                if (otherBegin == begin && otherEnd == end && BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(index + 8)) == unwind)
                    found = true;
            }
            if (begin < match || end <= begin || end - begin > 65536 || (unwind == 0 ? overlapping : !found))
                throw new InvalidOperationException("Furniture function extent changed: " + definition.Name);
            Pin(pe, begin, end - begin);
        }
    }

    private void Pin(PEReader pe, int begin, int length)
    {
        if (!pe.PEHeaders.SectionHeaders.Any(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0
            && begin >= section.VirtualAddress && (long)begin + length <= (long)section.VirtualAddress + Math.Min(section.VirtualSize, section.SizeOfRawData)))
            throw new InvalidOperationException("Furniture function is outside backed executable code.");
        proofs.Add(ModuleBase + begin, pe.GetSectionData(begin).GetContent(0, length).ToArray());
    }

    private void RequireBranch(PEReader pe, nint instruction, int displacementOffset, int length, nint expected)
    {
        var data = pe.GetSectionData(checked((int)(instruction - ModuleBase))).GetContent(0, length).ToArray();
        if ((displacementOffset == 1 ? data[0] is not (0xE8 or 0xE9) : data[0] != 0x0F || data[1] != 0x85)
            || instruction + length + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(displacementOffset)) != expected)
            throw new InvalidOperationException("Furniture native dispatch branch changed.");
    }

    private nint ResolveTableEntry(PEReader pe, nint table, int slot)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(pe.GetSectionData(checked((int)(table - ModuleBase)) + slot * 8).GetContent(0, 8).AsSpan());
        var rva = checked((int)(value - pe.PEHeaders.PEHeader!.ImageBase));
        var address = ModuleBase + rva;
        tables.Add((table + slot * 8, address)); return address;
    }

    private nint FindReceiverTable(PEReader pe, nint receiver, int reviewedRva)
    {
        var expected = pe.PEHeaders.PEHeader!.ImageBase + (ulong)(receiver - ModuleBase);
        var match = -1;
        foreach (var section in pe.PEHeaders.SectionHeaders)
        {
            if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0) continue;
            var data = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.VirtualSize, section.SizeOfRawData)).ToArray();
            for (var offset = 0; offset <= data.Length - 8; offset += 8)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) != expected) continue;
                if (match >= 0) throw new InvalidOperationException("Ambiguous furniture listener table.");
                match = section.VirtualAddress + offset;
            }
        }
        if (match != reviewedRva) throw new InvalidOperationException("Furniture listener table changed.");
        tables.Add((ModuleBase + match, receiver)); return ModuleBase + match;
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        foreach (var (address, proof) in proofs)
        {
            var actual = NearbyZoneNativeBinding.Read(address, proof.Length);
            if (installed.TryGetValue(address, out var prefix))
            {
                if (!actual.AsSpan(0, prefix.Length).SequenceEqual(prefix) || !actual.AsSpan(prefix.Length).SequenceEqual(proof.AsSpan(prefix.Length)))
                    throw new InvalidOperationException("Furniture observer or surrounding native function changed.");
                if (installedPaths.TryGetValue(address, out var dependencies))
                    foreach (var (location, expected) in dependencies)
                        if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected))
                            throw new InvalidOperationException("Furniture inherited forwarding path changed while observing.");
            }
            else if (!actual.AsSpan().SequenceEqual(proof)
                && !HasVerifiedRetirement(address, actual, proof)
                && !HasVerifiedForwarding(address, proof))
                throw new InvalidOperationException($"Furniture native entry at RVA 0x{address - ModuleBase:X} has an unrecognised or active modification; it does not match an owned observer or a verified inactive forwarding path.");
        }
        foreach (var (address, expected) in tables)
            if (ReadPointer(address) != expected) throw new InvalidOperationException("Furniture live vtable changed.");
    }

    internal void RecordInstalled(string name)
    {
        var address = Function(name);
        if (installed.ContainsKey(address)) throw new InvalidOperationException("Furniture observer prefix already owned.");
        var previous = forwarding.GetValueOrDefault(address) ?? retiredPaths.GetValueOrDefault(address);
        var dependencies = new Dictionary<nint, byte[]>();
        if (previous != null)
        {
            foreach (var entry in previous)
                if (entry.Key != address) dependencies.Add(entry.Key, entry.Value);
        }
        if (!originals.TryGetValue(address, out var originalProof)) throw new InvalidOperationException("Furniture Original was not verified before activation.");
        foreach (var entry in originalProof) dependencies[entry.Key] = entry.Value;
        installedPaths.Add(address, dependencies);
        installed.Add(address, NearbyZoneNativeBinding.Read(address, Math.Min(32, proofs[address].Length)));
        forwarding.Remove(address);
        Validate();
    }

    internal void RecordOriginal(string name, nint original)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var address = Function(name);
        if (!FurnitureRestoreForwardingProof.TryCaptureOriginal(address, NearbyZoneNativeBinding.Read(address, Math.Min(32, proofs[address].Length)), original, out var snapshot))
            throw new InvalidOperationException("Furniture Original trampoline could not be verified before activation: " + name);
        originals.Add(address, snapshot);
    }

    private bool HasVerifiedForwarding(nint address, byte[] proof)
    {
        if (!forwarding.TryGetValue(address, out var snapshots))
        {
            var name = functions.FirstOrDefault(entry => entry.Value == address).Key;
            if (name == null || !FurnitureRestoreForwardingProof.TryCapture(name, address, proof, out snapshots)) return false;
            forwarding.Add(address, snapshots);
            Plugin.Log.Information("[AutoRestoreFurniture] Verified inactive forwarding path for {Function}; continuing after plugin reload.", name);
        }
        // The header alone is insufficient: a wrapper may be changed or enabled.
        // Recheck every code span and indirect pointer read by the proof.
        foreach (var (location, expected) in snapshots)
            if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected)) return false;
        return true;
    }

    private static bool HasVerifiedRetirement(nint address, byte[] actual, byte[] proof)
    {
        if (!retired.TryGetValue(address, out var prefix) || !MatchesPrefix(actual, proof, prefix)
            || !retiredPaths.TryGetValue(address, out var snapshots) || snapshots.Count == 0) return false;
        foreach (var (location, expected) in snapshots)
            if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected)) return false;
        return true;
    }
    private static bool MatchesPrefix(byte[] actual, byte[] proof, byte[] prefix)
        => actual.AsSpan(0, prefix.Length).SequenceEqual(prefix)
            && actual.AsSpan(prefix.Length).SequenceEqual(proof.AsSpan(prefix.Length));

    internal void RetireInstalled(string name)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var address = Function(name);
        // Called only after the gate is deactivated and Reloaded Disable succeeds.
        // Check the already-recorded bytes; never adopt a newly observed patch.
        if (!installed.TryGetValue(address, out var prefix)) return;
        var proof = proofs[address];
        var actual = NearbyZoneNativeBinding.Read(address, proof.Length);
        if (!actual.AsSpan().SequenceEqual(proof) && !MatchesPrefix(actual, proof, prefix))
            throw new InvalidOperationException("Furniture observer changed during retirement: " + name);
        if (actual.AsSpan().SequenceEqual(proof))
        {
            retired.Remove(address); retiredPaths.Remove(address);
            installed.Remove(address); installedPaths.Remove(address);
            return;
        }
        if (!originals.TryGetValue(address, out var originalProof)
            || !FurnitureRestoreForwardingProof.TryCaptureDisabledWrapper(address, prefix, originalProof.Keys.Single(), out var snapshots))
            throw new InvalidOperationException("Furniture disabled observer path could not be verified: " + name);
        if (installedPaths.TryGetValue(address, out var dependencies))
        {
            foreach (var (location, expected) in dependencies)
            {
                if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException("Furniture inherited forwarding path changed during retirement: " + name);
                snapshots[location] = expected;
            }
        }
        retired[address] = prefix;
        retiredPaths[address] = snapshots;
        installed.Remove(address);
        installedPaths.Remove(address);
    }
    internal static nint ReadPointer(nint address) { NearbyZoneNativeBinding.RequireReadable(address, 8); return Marshal.ReadIntPtr(address); }
    internal static ulong ReadUlong(nint address) { NearbyZoneNativeBinding.RequireReadable(address, 8); return unchecked((ulong)Marshal.ReadInt64(address)); }
    internal static uint ReadUInt(nint address) { NearbyZoneNativeBinding.RequireReadable(address, 4); return unchecked((uint)Marshal.ReadInt32(address)); }
    internal static byte ReadByte(nint address) { NearbyZoneNativeBinding.RequireReadable(address, 1); return Marshal.ReadByte(address); }

    internal byte ReadSize(bool exterior, ulong estate, bool stored)
    {
        Validate();
        if (exterior) return ((delegate* unmanaged<byte, byte>)Function("OutdoorSize"))((byte)estate);
        return stored ? ((delegate* unmanaged<ulong, byte>)Function("IndoorStoredSize"))(estate)
            : ((delegate* unmanaged<byte>)Function("IndoorPlacedSize"))();
    }
}
