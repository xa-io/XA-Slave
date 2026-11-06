using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XASlave.Data;
using XASlave.Localization;

namespace XASlave.Services;

internal sealed record XagmanCharacterSelectionCandidate(
    int Index, string CharacterNameWorld, ulong ContentId, bool Eligible);

internal sealed record XagmanCharacterSelectionMatch(
    List<int> Indices,
    int MissingCount,
    int ExcludedCount,
    int AmbiguousCount,
    int ConflictCount,
    List<string> UnresolvedCharacters);

/// <summary>Detached validation, JSON exchange and identity matching; callers own all state changes.</summary>
internal static class XagmanCharacterSelectionService
{
    internal const int MaxJsonLength = 1_048_576;
    internal const int MaxNameLength = 128;
    internal const int MaxCharacters = 5000;
    internal const int MaxCharacterKeyLength = 128;
    internal const int MaxJsonDepth = 8;
    private const string FormatName = "XASlave.Xagman.CharacterSelection";
    private const int SchemaVersion = 1;
    private static readonly string[] RootProperties = { "format", "schemaVersion", "name", "role", "characters" };
    private static readonly string[] MemberProperties = { "contentId", "characterNameWorld" };

    private enum MatchFailure
    {
        None,
        Missing,
        Excluded,
        Ambiguous,
        IdentityConflict,
    }

    internal static bool TryCreate(
        string name, XagmanRole role,
        IReadOnlyList<XagmanCharacterSelectionCandidate> roster,
        IReadOnlyCollection<int> selectedIndices,
        out XagmanCharacterSelection selection,
        out UiMessage message)
    {
        selection = new();
        if (!TryNormalizeRoster(roster, out var candidates, out message))
            return false;
        if (selectedIndices == null)
        {
            message = UiMessage.Authored("The selected character rows are unavailable.");
            return false;
        }

        var rows = candidates.ToDictionary(candidate => candidate.Index);
        var selected = new HashSet<int>(selectedIndices);
        var captured = new XagmanCharacterSelection { Name = name, Role = role };
        foreach (var index in selected.OrderBy(index => index))
        {
            if (!rows.TryGetValue(index, out var candidate))
            {
                message = UiMessage.Authored("The selected character rows are unavailable.");
                return false;
            }

            // Save membership independently of temporary exclusions; eligibility is checked on Load.
            captured.Characters.Add(new XagmanCharacterSelectionMember
            {
                ContentId = candidate.ContentId,
                CharacterNameWorld = candidate.CharacterNameWorld,
            });
        }

        return TryNormalize(captured, role, out selection, out message);
    }

    internal static bool TryDeserialize(
        string json, XagmanRole expectedRole,
        out XagmanCharacterSelection selection,
        out UiMessage message)
    {
        selection = new();
        message = InvalidJsonMessage();
        if (json == null)
            return false;
        if (json.Length > MaxJsonLength)
        {
            message = JsonLimitMessage();
            return false;
        }
        if (HasUnpairedSurrogate(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            if (!TryReadProperties(document.RootElement, RootProperties, out var root))
                return false;
            if (root["format"].ValueKind != JsonValueKind.String ||
                root["schemaVersion"].ValueKind != JsonValueKind.Number ||
                !root["schemaVersion"].TryGetInt32(out var version))
                return false;
            if (!string.Equals(root["format"].GetString(), FormatName, StringComparison.Ordinal) || version != SchemaVersion)
            {
                message = UiMessage.Authored("Unsupported character list format or schema version.");
                return false;
            }
            if (root["name"].ValueKind != JsonValueKind.String ||
                root["role"].ValueKind != JsonValueKind.String ||
                root["characters"].ValueKind != JsonValueKind.Array)
                return false;

            var roleText = root["role"].GetString();
            XagmanRole role;
            if (string.Equals(roleText, nameof(XagmanRole.Tony), StringComparison.OrdinalIgnoreCase))
                role = XagmanRole.Tony;
            else if (string.Equals(roleText, nameof(XagmanRole.FranchiseOwner), StringComparison.OrdinalIgnoreCase))
                role = XagmanRole.FranchiseOwner;
            else
            {
                message = UiMessage.Authored("Character lists support only Tony or Franchise Owner roles.");
                return false;
            }

            var members = root["characters"];
            if (members.GetArrayLength() > MaxCharacters)
            {
                message = CharacterLimitMessage();
                return false;
            }

            var parsed = new XagmanCharacterSelection { Name = root["name"].GetString()!, Role = role };
            foreach (var member in members.EnumerateArray())
            {
                if (!TryReadProperties(member, MemberProperties, out var fields))
                    return false;
                if (fields["characterNameWorld"].ValueKind != JsonValueKind.String ||
                    !TryReadContentId(fields["contentId"], out var contentId))
                {
                    message = UiMessage.Authored("Character list contains an invalid character identity or content ID.");
                    return false;
                }

                parsed.Characters.Add(new XagmanCharacterSelectionMember
                {
                    ContentId = contentId,
                    CharacterNameWorld = fields["characterNameWorld"].GetString()!,
                });
            }

            return TryNormalize(parsed, expectedRole, out selection, out message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or EncoderFallbackException or DecoderFallbackException)
        {
            message = InvalidJsonMessage();
            return false;
        }
    }

    internal static bool TrySerialize(
        XagmanCharacterSelection selection,
        out string json, out UiMessage message)
    {
        json = string.Empty;
        if (!TryNormalize(selection, null, out var normalized, out message))
            return false;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("name", normalized.Name);
            writer.WriteString("role", normalized.Role.ToString());
            writer.WriteStartArray("characters");
            foreach (var member in normalized.Characters)
            {
                writer.WriteStartObject();
                // Decimal strings survive JSON consumers whose numbers cannot represent all UInt64 values.
                writer.WriteString("contentId", member.ContentId.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("characterNameWorld", member.CharacterNameWorld);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var serialized = Encoding.UTF8.GetString(buffer.ToArray());
        if (serialized.Length > MaxJsonLength)
        {
            message = JsonLimitMessage();
            return false;
        }
        json = serialized;
        return true;
    }

    internal static bool TryMatch(
        XagmanCharacterSelection selection, XagmanRole expectedRole,
        IReadOnlyList<XagmanCharacterSelectionCandidate> roster,
        out XagmanCharacterSelectionMatch result,
        out UiMessage message)
    {
        result = new(new(), 0, 0, 0, 0, new());
        if (!TryNormalize(selection, expectedRole, out var normalized, out message) ||
            !TryNormalizeRoster(roster, out var candidates, out message))
            return false;

        var byKey = new Dictionary<string, List<XagmanCharacterSelectionCandidate>>(StringComparer.OrdinalIgnoreCase);
        var byContentId = new Dictionary<ulong, List<XagmanCharacterSelectionCandidate>>();
        foreach (var candidate in candidates)
        {
            if (!byKey.TryGetValue(candidate.CharacterNameWorld, out var keyRows))
                byKey[candidate.CharacterNameWorld] = keyRows = new();
            keyRows.Add(candidate);
            if (candidate.ContentId == 0)
                continue;
            if (!byContentId.TryGetValue(candidate.ContentId, out var idRows))
                byContentId[candidate.ContentId] = idRows = new();
            idRows.Add(candidate);
        }

        var resolved = new XagmanCharacterSelectionCandidate?[normalized.Characters.Count];
        var failures = new MatchFailure[normalized.Characters.Count];
        var memberRows = new Dictionary<int, List<int>>();
        for (var memberIndex = 0; memberIndex < normalized.Characters.Count; memberIndex++)
        {
            resolved[memberIndex] = ResolveCandidate(normalized.Characters[memberIndex], byKey, byContentId, out failures[memberIndex]);
            var candidate = resolved[memberIndex];
            if (candidate == null)
                continue;
            if (!memberRows.TryGetValue(candidate.Index, out var rowMembers))
                memberRows[candidate.Index] = rowMembers = new();
            rowMembers.Add(memberIndex);
        }

        // A renamed CID match and a separate key fallback may both reach the same row.
        // Reject every colliding member, including the earlier one, before producing any indices.
        foreach (var rowMembers in memberRows.Values)
        {
            if (rowMembers.Count <= 1)
                continue;
            foreach (var memberIndex in rowMembers)
                failures[memberIndex] = MatchFailure.Ambiguous;
        }

        var indices = new List<int>();
        var unresolved = new List<string>();
        var missing = 0;
        var excluded = 0;
        var ambiguous = 0;
        var conflicts = 0;
        for (var memberIndex = 0; memberIndex < normalized.Characters.Count; memberIndex++)
        {
            var candidate = resolved[memberIndex];
            if (failures[memberIndex] == MatchFailure.None && candidate != null && !candidate.Eligible)
                failures[memberIndex] = MatchFailure.Excluded;
            switch (failures[memberIndex])
            {
                case MatchFailure.None:
                    if (candidate != null)
                        indices.Add(candidate.Index);
                    break;
                case MatchFailure.Missing:
                    missing++;
                    break;
                case MatchFailure.Excluded:
                    excluded++;
                    break;
                case MatchFailure.Ambiguous:
                    ambiguous++;
                    break;
                case MatchFailure.IdentityConflict:
                    conflicts++;
                    break;
            }
            if (failures[memberIndex] != MatchFailure.None)
                unresolved.Add(normalized.Characters[memberIndex].CharacterNameWorld);
        }

        indices.Sort();
        result = new(indices, missing, excluded, ambiguous, conflicts, unresolved);
        message = UiMessage.Format(UiLocale.Key("Matched {0:N0} characters; missing: {1:N0}, excluded: {2:N0}, ambiguous: {3:N0}, conflicting IDs: {4:N0}."),
            indices.Count, missing, excluded, ambiguous, conflicts);
        return true;
    }

    private static XagmanCharacterSelectionCandidate? ResolveCandidate(
        XagmanCharacterSelectionMember member,
        Dictionary<string, List<XagmanCharacterSelectionCandidate>> byKey,
        Dictionary<ulong, List<XagmanCharacterSelectionCandidate>> byContentId,
        out MatchFailure failure)
    {
        failure = MatchFailure.None;
        XagmanCharacterSelectionCandidate candidate;
        if (member.ContentId != 0 && byContentId.TryGetValue(member.ContentId, out var idRows))
        {
            if (idRows.Count != 1)
            {
                failure = MatchFailure.Ambiguous;
                return null;
            }
            candidate = idRows[0];
        }
        else
        {
            if (!byKey.TryGetValue(member.CharacterNameWorld, out var keyRows))
            {
                failure = MatchFailure.Missing;
                return null;
            }
            if (keyRows.Count != 1)
            {
                failure = MatchFailure.Ambiguous;
                return null;
            }
            candidate = keyRows[0];
            if (member.ContentId != 0 && candidate.ContentId != 0 && member.ContentId != candidate.ContentId)
            {
                failure = MatchFailure.IdentityConflict;
                return null;
            }
        }

        if (byKey[candidate.CharacterNameWorld].Count != 1 ||
            (candidate.ContentId != 0 && byContentId[candidate.ContentId].Count != 1))
        {
            failure = MatchFailure.Ambiguous;
            return null;
        }
        return candidate;
    }

    private static bool TryNormalize(
        XagmanCharacterSelection selection, XagmanRole? expectedRole,
        out XagmanCharacterSelection normalized, out UiMessage message)
    {
        normalized = new();
        message = UiMessage.Authored("Character list data is invalid.");
        if (selection == null || selection.Characters == null)
            return false;
        if (!string.Equals(selection.Format, FormatName, StringComparison.Ordinal) || selection.SchemaVersion != SchemaVersion)
        {
            message = UiMessage.Authored("Unsupported character list format or schema version.");
            return false;
        }
        if ((selection.Role != XagmanRole.Tony && selection.Role != XagmanRole.FranchiseOwner) ||
            (expectedRole.HasValue && expectedRole.Value != XagmanRole.Tony && expectedRole.Value != XagmanRole.FranchiseOwner))
        {
            message = UiMessage.Authored("Character lists support only Tony or Franchise Owner roles.");
            return false;
        }
        if (expectedRole.HasValue && selection.Role != expectedRole.Value)
        {
            message = UiMessage.Authored("This character list belongs to a different Xagman role.");
            return false;
        }
        if (selection.Name == null || selection.Name.Any(char.IsControl) || HasUnpairedSurrogate(selection.Name) ||
            selection.Name.Trim().Length == 0 || selection.Name.Trim().Length > MaxNameLength)
        {
            message = UiMessage.Format(UiLocale.Key("Character list name must contain 1-{0} characters and no control characters."), MaxNameLength);
            return false;
        }
        if (selection.Characters.Count > MaxCharacters)
        {
            message = CharacterLimitMessage();
            return false;
        }

        var detached = new XagmanCharacterSelection { Name = selection.Name.Trim(), Role = selection.Role };
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentIds = new HashSet<ulong>();
        foreach (var member in selection.Characters)
        {
            if (member == null || !TryNormalizeCharacterKey(member.CharacterNameWorld, out var key))
            {
                message = UiMessage.Authored("Character list contains an invalid character identity or content ID.");
                return false;
            }
            if (!keys.Add(key) || (member.ContentId != 0 && !contentIds.Add(member.ContentId)))
            {
                message = UiMessage.Authored("Character list contains duplicate character keys or content IDs.");
                return false;
            }
            detached.Characters.Add(new XagmanCharacterSelectionMember { ContentId = member.ContentId, CharacterNameWorld = key });
        }

        normalized = detached;
        message = UiMessage.Authored("Character list is valid.");
        return true;
    }

    private static bool TryNormalizeRoster(
        IReadOnlyList<XagmanCharacterSelectionCandidate> roster,
        out List<XagmanCharacterSelectionCandidate> normalized, out UiMessage message)
    {
        normalized = new();
        message = UiMessage.Authored("The configured character roster contains invalid identities or repeated row indices.");
        if (roster == null)
            return false;
        var indices = new HashSet<int>();
        var detached = new List<XagmanCharacterSelectionCandidate>();
        foreach (var candidate in roster)
        {
            if (candidate == null || candidate.Index < 0 || !indices.Add(candidate.Index) ||
                !TryNormalizeCharacterKey(candidate.CharacterNameWorld, out var key))
                return false;
            detached.Add(new(candidate.Index, key, candidate.ContentId, candidate.Eligible));
        }
        normalized = detached;
        message = UiMessage.Authored("Character list is valid.");
        return true;
    }

    internal static bool TryNormalizeCharacterKey(string key, out string normalized)
    {
        normalized = string.Empty;
        if (key == null || key.Any(char.IsControl) || HasUnpairedSurrogate(key))
            return false;
        var separator = key.IndexOf('@');
        if (separator < 0 || separator != key.LastIndexOf('@'))
            return false;
        var name = key[..separator].Trim();
        var homeworld = key[(separator + 1)..].Trim();
        if (name.Length == 0 || homeworld.Length == 0 ||
            string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(homeworld, "Unknown", StringComparison.OrdinalIgnoreCase))
            return false;
        var combined = name + "@" + homeworld;
        if (combined.Length > MaxCharacterKeyLength)
            return false;
        normalized = combined;
        return true;
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    return true;
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
                return true;
        }
        return false;
    }

    private static bool TryReadProperties(
        JsonElement element, IReadOnlyCollection<string> expected,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                !properties.TryAdd(property.Name, property.Value))
                return false;
        }
        return properties.Count == expected.Count;
    }

    private static bool TryReadContentId(JsonElement element, out ulong contentId)
    {
        contentId = 0;
        string? text;
        if (element.ValueKind == JsonValueKind.String)
            text = element.GetString();
        else if (element.ValueKind == JsonValueKind.Number)
            text = element.GetRawText();
        else
            return false;
        if (string.IsNullOrEmpty(text) || text.Length > 20)
            return false;
        foreach (var character in text)
        {
            if (character < '0' || character > '9')
                return false;
        }
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out contentId);
    }

    private static UiMessage InvalidJsonMessage()
        => UiMessage.Authored("Character list JSON is invalid or contains unknown, missing or repeated properties.");

    private static UiMessage JsonLimitMessage()
        => UiMessage.Format(UiLocale.Key("Character list JSON must contain no more than {0:N0} characters."), MaxJsonLength);

    private static UiMessage CharacterLimitMessage()
        => UiMessage.Format(UiLocale.Key("Character lists support no more than {0:N0} characters."), MaxCharacters);
}
