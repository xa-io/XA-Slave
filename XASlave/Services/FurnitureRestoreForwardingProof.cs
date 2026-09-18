using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Iced.Intel;

namespace XASlave.Services;

// Recognizes an inactive legacy Reloaded observer by its entire forwarding path.
// This only reads memory: no entry restoration, hook adoption or native execution.
internal static class FurnitureRestoreForwardingProof
{
    // Called before publishing our own hook. Pin its native trampoline while the
    // input entry is still independently validated and the hook is not active.
    internal static bool TryCaptureOriginal(nint address, byte[] entry, nint original, out Dictionary<nint, byte[]> snapshots)
    {
        snapshots = new();
        try
        {
            if (address <= 0 || original <= 0 || original == address) return false;
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(entry));
            decoder.IP = (ulong)address;
            var instructions = new List<Instruction>();
            var stolen = 0;
            while (stolen < 7)
            {
                decoder.Decode(out var instruction);
                if (instruction.IsInvalid || instruction.Length == 0 || stolen + instruction.Length > 32) return false;
                if (instruction.FlowControl != FlowControl.Next
                    && !(stolen == 0 && entry.Length >= 7 && entry[0] == 0xFF && entry[1] == 0x24 && entry[2] == 0x25)) return false;
                instructions.Add(instruction); stolen += instruction.Length;
            }
            using var stream = new MemoryStream();
            if (!BlockEncoder.TryEncode(64, new InstructionBlock(new StreamCodeWriter(stream), instructions, (ulong)original), out _, out _)) return false;
            var expected = stream.ToArray();
            if (expected.Length is < 1 or > 128) return false;
            var actual = Read(original, expected.Length + 5, snapshots);
            return actual.AsSpan(0, expected.Length).SequenceEqual(expected)
                && actual[expected.Length] == 0xE9
                && original + expected.Length + 5 + BinaryPrimitives.ReadInt32LittleEndian(actual.AsSpan(expected.Length + 1, 4)) == address + stolen;
        }
        catch (Exception) { return false; }
    }

    // This narrower capture is only for an observer owned by this plugin after
    // its Reloaded Disable succeeds. The caller already verifies its entry and tail.
    internal static bool TryCaptureDisabledWrapper(nint address, byte[] prefix, nint expectedOriginal, out Dictionary<nint, byte[]> snapshots)
    {
        snapshots = new();
        if (address <= 0 || (prefix.Length is < 7 or > 32)) return false;
        try
        {
            var reads = new Dictionary<nint, byte[]>();
            if (!Read(address, prefix.Length, reads).AsSpan().SequenceEqual(prefix)
                || !TryJump(address, prefix, reads, out var wrapper, out _)
                || wrapper == address
                || !TryAbsoluteJump(wrapper, Read(wrapper, 7, reads), reads, out var original)
                || original == address || original == wrapper || original != expectedOriginal) return false;
            foreach (var (location, expected) in reads)
                if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected)) return false;
            snapshots = reads;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TryCapture(string name, nint address, byte[] proof, out Dictionary<nint, byte[]> snapshots)
    {
        snapshots = new();
        if ((name is not ("InventoryRestore" or "StorageRestore" or "IndoorDecision" or "OutdoorDecision" or "WarningOpen"))
            || (proof.Length is < 16 or > 4096) || address <= 0)
            return false;
        try
        {
            var reads = new Dictionary<nint, byte[]>();
            var actual = Read(address, proof.Length, reads);
            // XA's old direct hooks used the default absolute entry patch. Do
            // not admit relative hooks: nesting them can rewrite older returns.
            if (!TryAbsoluteJump(address, actual, reads, out var wrapper)) return false;
            const int jumpLength = 7;
            var instructions = DecodePrefix(address, proof, jumpLength, out var stolen);
            if (instructions == null || stolen > 32 || !actual.AsSpan(stolen).SequenceEqual(proof.AsSpan(stolen))) return false;
            for (var index = jumpLength; index < stolen; index++)
                if (actual[index] != 0x90) return false;

            // Disable replaces the reverse wrapper with an absolute indirect jump
            // to OriginalFunctionAddress. An active gate/callback is not accepted.
            if (wrapper == address || !TryAbsoluteJump(wrapper, Read(wrapper, 7, reads), reads, out var original)
                || original == address || original == wrapper) return false;
            using var stream = new MemoryStream();
            var writer = new StreamCodeWriter(stream);
            if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, (ulong)original), out _, out _)) return false;
            var relocated = stream.ToArray();
            if (relocated.Length is < 1 or > 128) return false;
            var trampoline = Read(original, relocated.Length + 5, reads);
            if (!trampoline.AsSpan(0, relocated.Length).SequenceEqual(relocated)
                || trampoline[relocated.Length] != 0xE9
                || original + relocated.Length + 5 + BinaryPrimitives.ReadInt32LittleEndian(trampoline.AsSpan(relocated.Length + 1, 4)) != address + stolen)
                return false;

            // Capture a consistent proof, including every indirect pointer slot.
            foreach (var (location, expected) in reads)
                if (!NearbyZoneNativeBinding.Read(location, expected.Length).AsSpan().SequenceEqual(expected)) return false;
            snapshots = reads;
            return true;
        }
        catch (Exception)
        {
            // Unknown or disappearing code is not an inactive observer proof.
            return false;
        }
    }

    private static List<Instruction>? DecodePrefix(nint address, byte[] proof, int minimum, out int stolen)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(proof));
        decoder.IP = (ulong)address;
        var instructions = new List<Instruction>();
        stolen = 0;
        while (stolen < minimum)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid || instruction.Length == 0 || stolen + instruction.Length > 32) return null;
            // The five original prologues need no FunctionPatcher rewrite. Refuse
            // branches rather than infer external hook chains or rewritten code.
            if (instruction.FlowControl != FlowControl.Next) return null;
            instructions.Add(instruction);
            stolen += instruction.Length;
        }
        return instructions;
    }

    private static bool TryJump(nint address, byte[] code, Dictionary<nint, byte[]> reads, out nint target, out int length)
    {
        target = 0; length = 0;
        // XA used default Reloaded options: absolute low-pointer entries only.
        // Relative entry hooks invoke a different stacking/return-address patcher
        // and cannot be admitted by this compatibility proof.
        if (!TryAbsoluteJump(address, code, reads, out target)) return false;
        length = 7;
        return true;
    }

    private static bool TryAbsoluteJump(nint address, byte[] code, Dictionary<nint, byte[]> reads, out nint target)
    {
        target = 0;
        // Reloaded's AssembleAbsoluteJump uses jmp qword [absolute low address].
        if (code.Length < 7 || code[0] != 0xFF || code[1] != 0x24 || code[2] != 0x25) return false;
        var pointer = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3, 4));
        if (pointer <= 0) return false;
        target = (nint)BinaryPrimitives.ReadInt64LittleEndian(Read((nint)pointer, 8, reads));
        return target > 0;
    }

    private static byte[] Read(nint address, int length, Dictionary<nint, byte[]> reads)
    {
        if (address <= 0 || length is < 1 or > 4096) throw new InvalidOperationException("Invalid furniture forwarding proof range.");
        var bytes = NearbyZoneNativeBinding.Read(address, length);
        if (reads.TryGetValue(address, out var previous))
        {
            var common = Math.Min(previous.Length, bytes.Length);
            if (!previous.AsSpan(0, common).SequenceEqual(bytes.AsSpan(0, common)))
                throw new InvalidOperationException("Furniture forwarding proof changed during capture.");
            if (previous.Length >= bytes.Length) return bytes;
        }
        reads[address] = bytes;
        return bytes;
    }
}
