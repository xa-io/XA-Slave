using System;
using System.Globalization;
using System.Text.Json;

namespace XASlave.Services;

public enum XagmanSnapshotSaveState
{
    Waiting,
    Succeeded,
    Failed,
}

public readonly record struct XagmanSnapshotSaveVerification(
    XagmanSnapshotSaveState State,
    string Detail,
    DateTime SavedAtUtc,
    DateTime RefreshedAtUtc);

public static class XagmanSnapshotSaveVerifier
{
    public const int MinimumPayloadVersion = 3;

    public static XagmanSnapshotSaveVerification Evaluate(
        string resultJson,
        ulong expectedContentId,
        DateTime requestedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return Waiting("XA Database has not returned a snapshot result yet.");

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadBoolean(root, "Success", out var success)
                || !TryReadBoolean(root, "Pending", out var pending))
            {
                return Failed("XA Database returned an invalid snapshot result.");
            }

            if (pending)
                return Waiting("XA Database is refreshing and saving the post-trade snapshot.");

            var summary = TryReadString(root, "Summary");
            if (!success)
            {
                return Failed(string.IsNullOrWhiteSpace(summary)
                    ? "XA Database skipped or failed the post-trade snapshot save."
                    : summary);
            }

            if (!root.TryGetProperty("PayloadVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var payloadVersion)
                || payloadVersion < MinimumPayloadVersion)
            {
                return Failed($"XA Database snapshot result v{MinimumPayloadVersion}+ is required. Update XA Database before continuing Xagman.");
            }

            if (!root.TryGetProperty("ContentId", out var contentIdElement)
                || contentIdElement.ValueKind != JsonValueKind.Number
                || !contentIdElement.TryGetUInt64(out var savedContentId)
                || savedContentId != expectedContentId)
            {
                return Failed("XA Database saved a different character than the owner currently being processed.");
            }

            if (!TryReadUtc(root, "SavedAtUtc", out var savedAtUtc))
                return Failed("XA Database did not return a valid snapshot save timestamp.");

            var normalizedRequestUtc = requestedAtUtc.Kind == DateTimeKind.Utc
                ? requestedAtUtc
                : requestedAtUtc.ToUniversalTime();
            // XA Database's persisted/IPC timestamp contract is second-granular. Compare against
            // the start of the request second so a refresh completed later in that same second is
            // not rejected after its sub-second precision is intentionally removed.
            normalizedRequestUtc = normalizedRequestUtc.AddTicks(-(normalizedRequestUtc.Ticks % TimeSpan.TicksPerSecond));
            if (savedAtUtc < normalizedRequestUtc)
                return Waiting("XA Database is still reporting the snapshot from before this request.");

            if (!TryReadUtc(root, "RefreshedAtUtc", out var refreshedAtUtc))
                return Failed("XA Database did not prove that live inventory was refreshed before the snapshot save.");
            if (refreshedAtUtc < normalizedRequestUtc)
                return Failed("XA Database saved cached data without a post-request live inventory refresh.");
            if (refreshedAtUtc > savedAtUtc)
                return Failed("XA Database returned inconsistent refresh and save timestamps.");

            return new XagmanSnapshotSaveVerification(
                XagmanSnapshotSaveState.Succeeded,
                string.IsNullOrWhiteSpace(summary) ? "Post-trade snapshot saved." : summary,
                savedAtUtc,
                refreshedAtUtc);
        }
        catch (JsonException ex)
        {
            return Failed($"XA Database returned an unreadable snapshot result: {ex.Message}");
        }
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static string TryReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryReadUtc(JsonElement root, string propertyName, out DateTime value)
    {
        value = DateTime.MinValue;
        var text = TryReadString(root, propertyName);
        return !string.IsNullOrWhiteSpace(text)
            && DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }

    private static XagmanSnapshotSaveVerification Waiting(string detail)
        => new(XagmanSnapshotSaveState.Waiting, detail, DateTime.MinValue, DateTime.MinValue);

    private static XagmanSnapshotSaveVerification Failed(string detail)
        => new(XagmanSnapshotSaveState.Failed, detail, DateTime.MinValue, DateTime.MinValue);
}
