using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace XASlave.Services;

internal sealed record AutoRetainerExclusionSnapshot(HashSet<ulong> ContentIds, HashSet<string> CharacterKeys);

/// <summary>Matches AutoRetainer's global character blacklist without changing its config or saved task rosters.</summary>
internal static class AutoRetainerExclusionPolicy
{
    internal static AutoRetainerExclusionSnapshot ReadSnapshot(JsonElement root)
    {
        var excluded = ReadExcludedContentIds(root);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excluded.Count == 0)
            return new AutoRetainerExclusionSnapshot(excluded, keys);
        if (!root.TryGetProperty("OfflineData", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AutoRetainer character identities are unavailable; exclusions cannot be checked.");
        foreach (var entry in entries.EnumerateArray())
        {
            if (!ShouldExcludeCharacter(entry, excluded))
                continue;
            if (!entry.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("World", out var world) || world.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || string.IsNullOrWhiteSpace(world.GetString()))
                throw new InvalidDataException("An excluded AutoRetainer character has no usable name/homeworld.");
            keys.Add($"{name.GetString()}@{world.GetString()}");
        }
        return new AutoRetainerExclusionSnapshot(excluded, keys);
    }

    internal static bool IsCharacterAllowed(string characterKey, long savedContentId, bool honorExclusions,
        AutoRetainerExclusionSnapshot? snapshot)
    {
        if (!honorExclusions)
            return true;
        if (snapshot == null)
            return false;
        return !snapshot.CharacterKeys.Contains(characterKey)
            && (savedContentId == 0 || !snapshot.ContentIds.Contains(unchecked((ulong)savedContentId)));
    }

    internal static HashSet<ulong> ReadExcludedContentIds(JsonElement root)
    {
        var excluded = new HashSet<ulong>();
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AutoRetainer config is not an object; import was cancelled.");
        if (!root.TryGetProperty("Blacklist", out var blacklist))
            return excluded;
        if (blacklist.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AutoRetainer's excluded-character list is unreadable; import was cancelled.");

        foreach (var entry in blacklist.EnumerateArray())
        {
            if (!TryReadBlacklistEntry(entry, out var cid))
                throw new InvalidDataException("An AutoRetainer excluded-character ID is unreadable; import was cancelled.");
            excluded.Add(cid);
        }

        return excluded;
    }

    internal static bool ShouldExcludeCharacter(JsonElement entry, HashSet<ulong> excluded)
    {
        if (excluded.Count == 0)
            return false;
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("CID", out var cidValue)
            || !TryReadContentId(cidValue, out var cid))
            throw new InvalidDataException("An AutoRetainer character ID cannot be checked against its exclusions; import was cancelled.");
        return excluded.Contains(cid);
    }

    private static bool TryReadBlacklistEntry(JsonElement entry, out ulong cid)
    {
        cid = 0;
        if (entry.ValueKind != JsonValueKind.Object)
            return TryReadContentId(entry, out cid);

        // ValueTuple serializes as Item1/Item2; also accept named CID/Name exports.
        var hasNamedId = entry.TryGetProperty("CID", out var namedId);
        var hasTupleId = entry.TryGetProperty("Item1", out var tupleId);
        if (!hasNamedId && !hasTupleId)
            return false;
        if (hasNamedId && !TryReadContentId(namedId, out cid))
            return false;
        if (hasTupleId)
        {
            if (!TryReadContentId(tupleId, out var tupleCid))
                return false;
            if (hasNamedId && cid != tupleCid)
                return false;
            cid = tupleCid;
        }

        return true;
    }

    private static bool TryReadContentId(JsonElement value, out ulong cid)
    {
        cid = 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetUInt64(out cid);
        if (value.ValueKind == JsonValueKind.String)
            return ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out cid);
        return false;
    }
}
