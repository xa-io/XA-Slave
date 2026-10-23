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
    DateTime RefreshedAtUtc)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, global::XASlave.Localization.UiMessage> Presentations = new();

    internal XagmanSnapshotSaveVerification(XagmanSnapshotSaveState state, global::XASlave.Localization.UiMessage detail, DateTime savedAtUtc, DateTime refreshedAtUtc)
        : this(state, Retain(detail), savedAtUtc, refreshedAtUtc) { }

    internal global::XASlave.Localization.UiMessage DetailMessage => Detail != null && Presentations.TryGetValue(Detail, out var message)
        ? message : global::XASlave.Localization.UiMessage.Raw(Detail ?? string.Empty);

    private static string Retain(global::XASlave.Localization.UiMessage message)
    {
        var original = new string(message.Original.AsSpan());
        if (original.Length > 0)
            Presentations.AddOrUpdate(original, message);
        return original;
    }
}

public static class XagmanSnapshotSaveVerifier
{
    public const int MinimumPayloadVersion = 3;

    public static XagmanSnapshotSaveVerification Evaluate(
        string resultJson,
        ulong expectedContentId,
        DateTime requestedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return Waiting(global::XASlave.Localization.UiMessage.Key("S37F92BB21DFA9176"));

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadBoolean(root, "Success", out var success)
                || !TryReadBoolean(root, "Pending", out var pending))
            {
                return Failed(global::XASlave.Localization.UiMessage.Key("S707065AD3029875C"));
            }

            if (pending)
                return Waiting(global::XASlave.Localization.UiMessage.Key("S09B01093B7059693"));

            var summary = TryReadString(root, "Summary");
            if (!success)
            {
                return Failed((string.IsNullOrWhiteSpace(summary) ? global::XASlave.Localization.UiMessage.Key("S2261B438DCF2C951") : global::XASlave.Localization.UiMessage.Raw(summary)));
            }

            if (!root.TryGetProperty("PayloadVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var payloadVersion)
                || payloadVersion < MinimumPayloadVersion)
            {
                return Failed(global::XASlave.Localization.UiMessage.Format("S144500AA7330040A", MinimumPayloadVersion));
            }

            if (!root.TryGetProperty("ContentId", out var contentIdElement)
                || contentIdElement.ValueKind != JsonValueKind.Number
                || !contentIdElement.TryGetUInt64(out var savedContentId)
                || savedContentId != expectedContentId)
            {
                return Failed(global::XASlave.Localization.UiMessage.Key("S1F5246BE5B6B2AFB"));
            }

            if (!TryReadUtc(root, "SavedAtUtc", out var savedAtUtc))
                return Failed(global::XASlave.Localization.UiMessage.Key("SD90020739896B633"));

            var normalizedRequestUtc = requestedAtUtc.Kind == DateTimeKind.Utc
                ? requestedAtUtc
                : requestedAtUtc.ToUniversalTime();
            // XA Database's persisted/IPC timestamp contract is second-granular. Compare against
            // the start of the request second so a refresh completed later in that same second is
            // not rejected after its sub-second precision is intentionally removed.
            normalizedRequestUtc = normalizedRequestUtc.AddTicks(-(normalizedRequestUtc.Ticks % TimeSpan.TicksPerSecond));
            if (savedAtUtc < normalizedRequestUtc)
                return Waiting(global::XASlave.Localization.UiMessage.Key("S37036BCE680CEF34"));

            if (!TryReadUtc(root, "RefreshedAtUtc", out var refreshedAtUtc))
                return Failed(global::XASlave.Localization.UiMessage.Key("S3B84D79CE2292464"));
            if (refreshedAtUtc < normalizedRequestUtc)
                return Failed(global::XASlave.Localization.UiMessage.Key("S47DCC23C323683CB"));
            if (refreshedAtUtc > savedAtUtc)
                return Failed(global::XASlave.Localization.UiMessage.Key("S16D06F34976DBE18"));

            return new XagmanSnapshotSaveVerification(
                XagmanSnapshotSaveState.Succeeded,
                (string.IsNullOrWhiteSpace(summary) ? global::XASlave.Localization.UiMessage.Key("S709A940FDFC38567") : global::XASlave.Localization.UiMessage.Raw(summary)),
                savedAtUtc,
                refreshedAtUtc);
        }
        catch (JsonException ex)
        {
            return Failed(global::XASlave.Localization.UiMessage.Format("SC6AEEA3B222A8326", global::XASlave.Localization.UiMessage.FromException(ex)));
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

    private static XagmanSnapshotSaveVerification Waiting(global::XASlave.Localization.UiMessage detail)
        => new(XagmanSnapshotSaveState.Waiting, detail, DateTime.MinValue, DateTime.MinValue);

    private static XagmanSnapshotSaveVerification Failed(global::XASlave.Localization.UiMessage detail)
        => new(XagmanSnapshotSaveState.Failed, detail, DateTime.MinValue, DateTime.MinValue);
}
