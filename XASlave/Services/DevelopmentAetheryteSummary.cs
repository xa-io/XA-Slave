using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XASlave.Services;

/// <summary>Bounded aggregate evidence, not a replacement for individual provider records or correctness checks.</summary>
internal static class DevelopmentAetheryteSummary
{
    internal const int MaximumSourceBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Create(string source, out string? summary)
    {
        summary = null;
        try
        {
            if (source.Length > MaximumSourceBytes || StrictUtf8.GetByteCount(source) > MaximumSourceBytes)
                return "response_oversize";
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RequireUniqueProperties(root);
            var contract = root.GetProperty("ipcContractVersion").GetInt32();
            var version = root.GetProperty("aetheryteListVersion").GetInt32();
            if (contract is not (7 or 8) || version != 1) return "dependency_invalid_evidence";
            var characters = root.GetProperty("characters");
            if (characters.ValueKind != JsonValueKind.Array) return "dependency_invalid_evidence";
            if (characters.GetArrayLength() > 4096) return "response_oversize";
            var owners = new HashSet<ulong>();
            var known = 0;
            var ids = 0;
            var destinations = 0;
            foreach (var character in characters.EnumerateArray())
            {
                RequireUniqueProperties(character);
                var owner = character.GetProperty("contentId").GetUInt64();
                if (owner == 0 || !owners.Add(owner)) return "dependency_invalid_evidence";
                if (character.GetProperty("known").GetBoolean()) known++;
                var characterIds = character.GetProperty("aetheryteIds");
                var characterDestinations = character.GetProperty("destinations");
                foreach (var id in characterIds.EnumerateArray()) _ = id.GetUInt32();
                // Count the complete arrays; this deliberately does not validate destination field semantics.
                ids = checked(ids + characterIds.GetArrayLength());
                destinations = checked(destinations + characterDestinations.GetArrayLength());
            }
            var bytes = StrictUtf8.GetBytes(source);
            summary = new JsonObject
            {
                ["schema"] = 1,
                ["representation"] = "aggregate-summary",
                ["sourceQuery"] = "xa.aetherytes.all",
                ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["sourceUtf8Bytes"] = bytes.Length,
                ["ipcContractVersion"] = contract,
                ["aetheryteListVersion"] = version,
                ["characterCount"] = owners.Count,
                ["knownCharacterCount"] = known,
                ["unknownCharacterCount"] = owners.Count - known,
                ["aetheryteIdCount"] = ids,
                ["destinationCount"] = destinations,
                ["scannedAllReturnedCharacters"] = true,
                ["recordsRetained"] = false,
                ["providerPopulationVerified"] = false,
                ["validation"] = "aggregate-fields-only",
            }.ToJsonString();
            return "observed";
        }
        catch
        {
            return "dependency_invalid_evidence";
        }
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException("Duplicate evidence field.");
    }
}
