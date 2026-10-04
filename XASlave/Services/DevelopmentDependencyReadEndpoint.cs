using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XASlave.Services;

/// <summary>Fixed, read-only access to XA Slave's own dependency wrappers for development verification.</summary>
internal static class DevelopmentDependencyReadEndpoint
{
    private const int MaxRequestBytes = 1024;
    private const int MaxValueBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly record struct ReadRequest(string Query, ulong ContentId, uint AetheryteId);

    internal static string Read(string? json, IpcClient ipc, bool targetAvailable)
    {
        if (!TryParseRequest(json, out var request))
            return Response(string.Empty, "invalid_request", null);
        if (!targetAvailable)
            return Response(request.Query, "target_unavailable", null);

        try
        {
            bool success;
            object? value;
            switch (request.Query)
            {
                case "ar.multimode":
                    success = ipc.TryGetAutoRetainerMultiModeEnabled(out var multimode);
                    value = multimode;
                    break;
                case "ar.busy":
                    success = ipc.TryGetAutoRetainerBusy(out var arBusy);
                    value = arBusy;
                    break;
                case "lifestream.busy":
                    success = ipc.TryGetLifestreamBusy(out var lsBusy);
                    value = lsBusy;
                    break;
                case "xa.aetherytes.character":
                    success = ipc.TryGetCharacterAetherytesJson(request.ContentId, out var characterJson, out _);
                    value = characterJson;
                    break;
                case "xa.aetherytes.all":
                    success = ipc.TryGetAllCharacterAetherytesJson(out var allJson, out _);
                    value = allJson;
                    break;
                case "xa.aetherytes.all-summary":
                    if (!ipc.TryGetAllCharacterAetherytesJson(out var summarySource, out _))
                        return Response(request.Query, "dependency_unavailable", null);
                    var summaryCode = DevelopmentAetheryteSummary.Create(summarySource, out var summary);
                    return Response(request.Query, summaryCode, summary);
                case "xa.aetherytes.has":
                    success = ipc.TryHasAetheryte(request.AetheryteId, out var hasAccess, out _);
                    value = hasAccess;
                    break;
                case "xa.aetherytes.character-has":
                    success = ipc.TryCharacterHasAetheryte(request.ContentId, request.AetheryteId, out var characterHasAccess, out _);
                    value = characterHasAccess;
                    break;
                default:
                    return Response(string.Empty, "invalid_request", null);
            }

            if (!success)
                return Response(request.Query, "dependency_unavailable", null);
            if (value is string text && StrictUtf8.GetByteCount(text) > MaxValueBytes)
                return Response(request.Query, "response_oversize", null);
            return Response(request.Query, "observed", value);
        }
        catch
        {
            return Response(request.Query, "dependency_unavailable", null);
        }
    }

    private static bool TryParseRequest(string? json, out ReadRequest request)
    {
        request = default;
        try
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxRequestBytes || StrictUtf8.GetByteCount(json) > MaxRequestBytes)
                return false;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    return false;
            }

            if (!root.TryGetProperty("schema", out var schema) || schema.GetRawText() != "1"
                || !root.TryGetProperty("query", out var queryValue) || queryValue.ValueKind != JsonValueKind.String)
                return false;
            var query = queryValue.GetString()!;
            ulong contentId = 0;
            uint aetheryteId = 0;
            var expectsContent = query == "xa.aetherytes.character" || query == "xa.aetherytes.character-has";
            var expectsAetheryte = query == "xa.aetherytes.has" || query == "xa.aetherytes.character-has";
            if (query != "ar.multimode" && query != "ar.busy" && query != "lifestream.busy"
                && query != "xa.aetherytes.all" && query != "xa.aetherytes.all-summary" && !expectsContent && !expectsAetheryte)
                return false;
            if (names.Count != 2 + (expectsContent ? 1 : 0) + (expectsAetheryte ? 1 : 0))
                return false;
            if (expectsContent && !TryReadContentId(root, out contentId))
                return false;
            if (expectsAetheryte && !TryReadAetheryteId(root, out aetheryteId))
                return false;
            request = new ReadRequest(query, contentId, aetheryteId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadContentId(JsonElement root, out ulong value)
    {
        value = 0;
        if (!root.TryGetProperty("contentId", out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value.ToString(CultureInfo.InvariantCulture) == text;
    }

    private static bool TryReadAetheryteId(JsonElement root, out uint value)
    {
        value = 0;
        if (!root.TryGetProperty("aetheryteId", out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value.ToString(CultureInfo.InvariantCulture) == text;
    }

    private static string Response(string query, string code, object? value)
        => JsonSerializer.Serialize(new { schema = 1, query, success = code == "observed", code, value });
}
