using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace XASlave.Services;

/// <summary>Keeps completed forecast evidence separate from an in-progress inventory scan.</summary>
internal sealed class XagmanForecastRefreshState
{
    private string inputKey = string.Empty;
    public string ContentKey { get; private set; } = string.Empty;
    public DateTime VerifiedAtUtc { get; private set; } = DateTime.MinValue;

    public bool IsCurrent(string currentInputKey, DateTime now, TimeSpan maxAge)
        => VerifiedAtUtc != DateTime.MinValue && now >= VerifiedAtUtc
            && now - VerifiedAtUtc <= maxAge
            && inputKey.Equals(currentInputKey, StringComparison.Ordinal);

    // Call only after all queries have resolved. An unchanged completed scan renews evidence,
    // but does not require replacing displayed totals or advancing the content revision.
    public bool Commit(string currentInputKey, string contentKey, DateTime now)
    {
        var changed = VerifiedAtUtc == DateTime.MinValue
            || !inputKey.Equals(currentInputKey, StringComparison.Ordinal)
            || !ContentKey.Equals(contentKey, StringComparison.Ordinal);
        inputKey = currentInputKey;
        ContentKey = contentKey;
        VerifiedAtUtc = now;
        return changed;
    }

    public void Reset()
    {
        inputKey = string.Empty;
        ContentKey = string.Empty;
        VerifiedAtUtc = DateTime.MinValue;
    }

    public static string SemanticKey<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteSemanticValue(writer, JsonSerializer.SerializeToElement(value));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSemanticValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (property.Name is "GeneratedAtUtc" or "Revision")
                    continue;
                writer.WritePropertyName(property.Name);
                WriteSemanticValue(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray())
                WriteSemanticValue(writer, element);
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}
