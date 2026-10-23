using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    internal static string Format(object? value, string memberPath) => FormatMessage(value, memberPath).Original;

    internal static global::XASlave.Localization.UiMessage FormatMessage(object? value, string memberPath)
    {
        memberPath = memberPath?.Trim() ?? string.Empty;
        if (memberPath.Length > 1024) throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("S40426ABA6F001B57")), global::XASlave.Localization.UiMessage.Key("S40426ABA6F001B57"));
        var segments = memberPath.Length == 0 ? Array.Empty<string>() : memberPath.Split('.');
        if (segments.Length > MaxPathSegments || segments.Any(s => s.Length is 0 or > 128))
            throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("SEA70D931C5A2C27B")), global::XASlave.Localization.UiMessage.Key("SEA70D931C5A2C27B"));
        if (value is string json && json.Length <= MaxJson && (json.TrimStart().StartsWith('{') || json.TrimStart().StartsWith('[')))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
            catch (JsonException)
            {
                if (segments.Length != 0) throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("S9262D17647A44692")), global::XASlave.Localization.UiMessage.Key("S9262D17647A44692"));
                return global::XASlave.Localization.UiMessage.Raw(Truncate(json));
            }
            using (document) return Project(document.RootElement, segments);
        }
        return Project(value, segments);
    }

    private static global::XASlave.Localization.UiMessage Project(object? value, string[] segments)
    {
        foreach (var segment in segments) value = Select(value, segment);
        var result = new ProjectionOutput();
        Append(result, value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return result.Message;
    }

    private static object? Select(object? value, string segment)
    {
        if (value is null) throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("SDD055D7284EB3A9E")), global::XASlave.Localization.UiMessage.Key("SDD055D7284EB3A9E"));
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(segment, out var property)) return property;
            if (json.ValueKind == JsonValueKind.Array && TryIndex(segment, json.GetArrayLength(), out var jsonIndex)) return json[jsonIndex];
            throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("SBD2AD79719410542")), global::XASlave.Localization.UiMessage.Key("SBD2AD79719410542"));
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
            throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("SC27D537B2F7500D6")), global::XASlave.Localization.UiMessage.Key("SC27D537B2F7500D6"));
        }
        var field = ReadableFields(type).FirstOrDefault(f => f.Name == segment);
        if (field.Field != null) return field.Field.GetValue(value);
        throw global::XASlave.Localization.UiMessage.Attach(new InvalidOperationException(global::XASlave.Localization.UiLocale.Native("S3B225C53659B9D57")), global::XASlave.Localization.UiMessage.Key("S3B225C53659B9D57"));
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

    private static void Append(ProjectionOutput output, object? value, int depth, HashSet<object> seen)
    {
        if (output.Length >= MaxText) return;
        if (Scalar(value) is { } scalar) { Write(output, scalar); return; }
        if (value is JsonElement json) { AppendJson(output, json, depth, seen); return; }
        if (value == null) return;
        var type = value.GetType();
        if (depth >= MaxDepth) { Write(output, "<" + type.Name + ">"); return; }
        if (!type.IsValueType && !seen.Add(value)) { Write(output, global::XASlave.Localization.UiMessage.Key("SDA1C655DAF1F74FF")); return; }
        if (value is Array array && array.Rank == 1 && array.GetLowerBound(0) == 0)
        {
            for (var i = 0; i < Math.Min(array.Length, MaxEntries) && output.Length < MaxText; i++)
            {
                Entry(output, i.ToString(CultureInfo.InvariantCulture), array.GetValue(i), depth, seen);
            }
            if (array.Length > MaxEntries) Write(output, global::XASlave.Localization.UiMessage.Key("S1F3B5F0B42A18F88"));
            if (array.Length == 0) Write(output, "[]");
            return;
        }
        if (IsExactGeneric(type, typeof(List<>)) && value is IList list)
        {
            for (var i = 0; i < Math.Min(list.Count, MaxEntries) && output.Length < MaxText; i++)
                Entry(output, i.ToString(CultureInfo.InvariantCulture), list[i], depth, seen);
            if (list.Count > MaxEntries) Write(output, global::XASlave.Localization.UiMessage.Key("S1F3B5F0B42A18F88"));
            if (list.Count == 0) Write(output, "[]");
            return;
        }
        if (IsExactGeneric(type, typeof(Dictionary<,>)) && value is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxEntries || output.Length >= MaxText) break;
                EntryMessage(output, Scalar(entry.Key) is { } entryKey ? global::XASlave.Localization.UiMessage.Raw(entryKey) : global::XASlave.Localization.UiMessage.Key("S8ADBD890B68A0061"), entry.Value, depth, seen);
            }
            if (dictionary.Count > MaxEntries) Write(output, global::XASlave.Localization.UiMessage.Key("S1F3B5F0B42A18F88"));
            if (dictionary.Count == 0) Write(output, "{}");
            return;
        }
        var fields = ReadableFields(type).Take(MaxEntries + 1).ToArray();
        foreach (var field in fields.Take(MaxEntries))
        {
            if (output.Length >= MaxText) break;
            Entry(output, field.Name, field.Field.GetValue(value), depth, seen);
        }
        if (fields.Length > MaxEntries) Write(output, global::XASlave.Localization.UiMessage.Key("SE6C4B2AB03DE6B1B"));
        if (fields.Length == 0) Write(output, global::XASlave.Localization.UiMessage.Format("S558D34FFD9869B9F" , type.Name));
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

    private static void AppendJson(ProjectionOutput output, JsonElement json, int depth, HashSet<object> seen)
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
            if (count > MaxEntries) Write(output, global::XASlave.Localization.UiMessage.Key("S1F3B5F0B42A18F88"));
            if (count == 0) Write(output, json.ValueKind == JsonValueKind.Object ? "{}" : "[]");
        }
        else Write(output, json.ValueKind == JsonValueKind.String ? json.GetString() ?? "null" : json.GetRawText());
    }

    private static void Entry(ProjectionOutput output, string name, object? value, int depth, HashSet<object> seen)
        => EntryMessage(output, global::XASlave.Localization.UiMessage.Raw(name), value, depth, seen);

    private static void EntryMessage(ProjectionOutput output, global::XASlave.Localization.UiMessage name, object? value, int depth, HashSet<object> seen)
    {
        if (output.Length > 0) Write(output, "\n");
        Write(output, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Raw(new string(' ', depth * 2)), name, global::XASlave.Localization.UiMessage.Raw(": ")));
        Append(output, value, depth + 1, seen);
    }

    private static string Truncate(string text) => text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";

    // Only captured text is retained. Drawing never reflects provider objects again.
    private sealed class ProjectionOutput
    {
        private readonly List<global::XASlave.Localization.UiMessage> pieces = new();
        internal int Length { get; private set; }
        internal global::XASlave.Localization.UiMessage Message => global::XASlave.Localization.UiMessage.Truncate(global::XASlave.Localization.UiMessage.Concat(pieces.ToArray()), MaxText, ellipsis: true);
        internal void Add(global::XASlave.Localization.UiMessage text)
        {
            pieces.Add(text);
            Length += text.Original.Length;
        }
    }

    private static void Write(ProjectionOutput output, string text) => Write(output, global::XASlave.Localization.UiMessage.Raw(text));

    private static void Write(ProjectionOutput output, global::XASlave.Localization.UiMessage text)
    {
        var remaining = MaxText - output.Length;
        if (remaining <= 0) return;
        output.Add(global::XASlave.Localization.UiMessage.Truncate(text, remaining, ellipsis: true));
    }
}
