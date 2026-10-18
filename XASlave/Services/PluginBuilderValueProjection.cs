using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace XASlave.Services;

/// <summary>Detaches a bounded display value without serializing target objects or executing their getters.</summary>
internal static class PluginBuilderValueProjection
{
    private const int MaxText = 4096;
    private const int MaxJson = 32768;
    private const int MaxEntries = 12;
    private const int MaxDepth = 3;
    private const int MaxPathSegments = 8;
    private const int MaxDictionarySearch = 256;

    internal static string Format(object? value, string memberPath)
    {
        memberPath = memberPath?.Trim() ?? string.Empty;
        if (memberPath.Length > 1024) throw new InvalidOperationException("The value path is too long.");
        var segments = memberPath.Length == 0 ? Array.Empty<string>() : memberPath.Split('.');
        if (segments.Length > MaxPathSegments || segments.Any(s => s.Length is 0 or > 128))
            throw new InvalidOperationException("Use up to eight field, key or index segments separated by dots.");
        if (value is string json && json.Length <= MaxJson && (json.TrimStart().StartsWith('{') || json.TrimStart().StartsWith('[')))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
            catch (JsonException)
            {
                if (segments.Length != 0) throw new InvalidOperationException("The returned text is not supported JSON.");
                return Truncate(json);
            }
            using (document) return Project(document.RootElement, segments);
        }
        return Project(value, segments);
    }

    private static string Project(object? value, string[] segments)
    {
        foreach (var segment in segments) value = Select(value, segment);
        var result = new StringBuilder();
        Append(result, value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return result.ToString();
    }

    private static object? Select(object? value, string segment)
    {
        if (value is null) throw new InvalidOperationException("The selected path passes through null.");
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(segment, out var property)) return property;
            if (json.ValueKind == JsonValueKind.Array && TryIndex(segment, json.GetArrayLength(), out var jsonIndex)) return json[jsonIndex];
            throw new InvalidOperationException("The selected JSON field or index is unavailable.");
        }
        if (value is Array array && array.Rank == 1 && array.GetLowerBound(0) == 0 && TryIndex(segment, array.Length, out var arrayIndex))
            return array.GetValue(arrayIndex);
        var type = value.GetType();
        if (IsExactGeneric(type, typeof(List<>)) && value is IList list && TryIndex(segment, list.Count, out var index)) return list[index];
        if (IsExactGeneric(type, typeof(Dictionary<,>)) && value is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxDictionarySearch) break;
                if (Scalar(entry.Key) is { } key && string.Equals(key, segment, StringComparison.Ordinal)) return entry.Value;
            }
            throw new InvalidOperationException("The dictionary key was not found within the first 256 entries.");
        }
        var field = ReadableFields(type).FirstOrDefault(f => f.Name == segment);
        if (field.Field != null) return field.Field.GetValue(value);
        throw new InvalidOperationException("The selected public field or automatic property is unavailable.");
    }

    private static bool TryIndex(string value, int count, out int index)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < count;

    private static bool IsExactGeneric(Type type, Type definition)
        => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    private static string? Scalar(object? value) => value switch
    {
        null => "null",
        string text => Truncate(text),
        bool boolean => boolean ? "true" : "false",
        char character => character.ToString(),
        byte number => number.ToString(CultureInfo.InvariantCulture),
        sbyte number => number.ToString(CultureInfo.InvariantCulture),
        short number => number.ToString(CultureInfo.InvariantCulture),
        ushort number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        uint number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        float number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        Guid id => id.ToString("D"),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
        Enum enumeration => enumeration.ToString(),
        _ => null,
    };

    private static void Append(StringBuilder output, object? value, int depth, HashSet<object> seen)
    {
        if (output.Length >= MaxText) return;
        if (Scalar(value) is { } scalar) { Write(output, scalar); return; }
        if (value is JsonElement json) { AppendJson(output, json, depth, seen); return; }
        if (value == null) return;
        var type = value.GetType();
        if (depth >= MaxDepth) { Write(output, "<" + type.Name + ">"); return; }
        if (!type.IsValueType && !seen.Add(value)) { Write(output, "<already shown>"); return; }
        if (value is Array array && array.Rank == 1 && array.GetLowerBound(0) == 0)
        {
            for (var i = 0; i < Math.Min(array.Length, MaxEntries) && output.Length < MaxText; i++)
            {
                Entry(output, i.ToString(CultureInfo.InvariantCulture), array.GetValue(i), depth, seen);
            }
            if (array.Length > MaxEntries) Write(output, "\n… more entries");
            if (array.Length == 0) Write(output, "[]");
            return;
        }
        if (IsExactGeneric(type, typeof(List<>)) && value is IList list)
        {
            for (var i = 0; i < Math.Min(list.Count, MaxEntries) && output.Length < MaxText; i++)
                Entry(output, i.ToString(CultureInfo.InvariantCulture), list[i], depth, seen);
            if (list.Count > MaxEntries) Write(output, "\n… more entries");
            if (list.Count == 0) Write(output, "[]");
            return;
        }
        if (IsExactGeneric(type, typeof(Dictionary<,>)) && value is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxEntries || output.Length >= MaxText) break;
                Entry(output, Scalar(entry.Key) ?? "<key>", entry.Value, depth, seen);
            }
            if (dictionary.Count > MaxEntries) Write(output, "\n… more entries");
            if (dictionary.Count == 0) Write(output, "{}");
            return;
        }
        var fields = ReadableFields(type).Take(MaxEntries + 1).ToArray();
        foreach (var field in fields.Take(MaxEntries))
        {
            if (output.Length >= MaxText) break;
            Entry(output, field.Name, field.Field.GetValue(value), depth, seen);
        }
        if (fields.Length > MaxEntries) Write(output, "\n… more fields");
        if (fields.Length == 0) Write(output, "<" + type.Name + ": select a readable field>");
    }

    private static IEnumerable<(string Name, FieldInfo Field)> ReadableFields(Type type)
    {
        // Only managed instance fields and compiler-generated backing fields. No getters or static initializers.
        if (type.IsPointer || type.IsByRefLike || typeof(Delegate).IsAssignableFrom(type)) yield break;
        var visited = 0;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (visited++ >= 128) yield break;
            if (!field.FieldType.IsPointer && !field.FieldType.IsByRefLike) yield return (field.Name, field);
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (visited++ >= 128) yield break;
            if (property.GetIndexParameters().Length != 0) continue;
            var field = property.DeclaringType?.GetField("<" + property.Name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.IsDefined(typeof(CompilerGeneratedAttribute), false)
                && !field.FieldType.IsPointer && !field.FieldType.IsByRefLike)
                yield return (property.Name, field);
        }
    }

    private static void AppendJson(StringBuilder output, JsonElement json, int depth, HashSet<object> seen)
    {
        if (json.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (depth >= MaxDepth) { Write(output, "<JSON " + json.ValueKind + ">"); return; }
            var count = 0;
            if (json.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in json.EnumerateObject())
                {
                    if (count++ >= MaxEntries || output.Length >= MaxText) break;
                    Entry(output, entry.Name, entry.Value, depth, seen);
                }
            }
            else
            {
                foreach (var entry in json.EnumerateArray())
                {
                    if (count >= MaxEntries || output.Length >= MaxText) break;
                    Entry(output, (count++).ToString(CultureInfo.InvariantCulture), entry, depth, seen);
                }
                if (json.GetArrayLength() > MaxEntries) count = MaxEntries + 1;
            }
            if (count > MaxEntries) Write(output, "\n… more entries");
            if (count == 0) Write(output, json.ValueKind == JsonValueKind.Object ? "{}" : "[]");
        }
        else Write(output, json.ValueKind == JsonValueKind.String ? json.GetString() ?? "null" : json.GetRawText());
    }

    private static void Entry(StringBuilder output, string name, object? value, int depth, HashSet<object> seen)
    {
        if (output.Length > 0) Write(output, "\n");
        Write(output, new string(' ', depth * 2) + name + ": ");
        Append(output, value, depth + 1, seen);
    }

    private static string Truncate(string text) => text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";

    private static void Write(StringBuilder output, string text)
    {
        var remaining = MaxText - output.Length;
        if (remaining <= 0) return;
        if (text.Length <= remaining) output.Append(text);
        else { output.Append(text.AsSpan(0, remaining - 1)); output.Append('…'); }
    }
}
