using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XASlave.Data;

namespace XASlave.Services;

internal sealed record AethertekWorldStatus(string Name, string DataCenter, string Status, string Category, bool CreationAllowed);

internal sealed record AethertekWorldSelection(
    Dictionary<string, string> Worlds, IReadOnlyList<string> UnavailableDataCenters, DateTimeOffset CheckedAt);

internal static class AethertekServerStatus
{
    internal const string StatusUrl = "https://online.aethertek.io/data/status.json";

    internal static async Task<AethertekWorldSelection> FetchAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 2 * 1024 * 1024,
        };
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XA-Slave/1.0");
        var json = await client.GetStringAsync(StatusUrl, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(json, DateTimeOffset.UtcNow);
    }

    internal static bool IsFresh(long timestamp, long now, long staleAfterSeconds)
        => timestamp > 0 && timestamp <= now + 60 && now - timestamp <= staleAfterSeconds;

    internal static bool IsEligible(AethertekWorldStatus world)
        => world.Status.Equals("online", StringComparison.OrdinalIgnoreCase)
            && world.CreationAllowed
            && (world.Category.Equals("Standard", StringComparison.OrdinalIgnoreCase)
                || world.Category.Equals("Preferred", StringComparison.OrdinalIgnoreCase)
                || world.Category.Equals("Preferred+", StringComparison.OrdinalIgnoreCase)
                || world.Category.Equals("New", StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, string> SelectWorlds(IEnumerable<AethertekWorldStatus> worlds)
    {
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var world in worlds.OrderBy(world => world.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsEligible(world) && !selected.ContainsKey(world.DataCenter))
                selected.Add(world.DataCenter, world.Name);
        }
        return selected;
    }

    internal static Dictionary<string, string> CompleteSelections(
        IReadOnlyDictionary<string, string> eligible, IReadOnlyDictionary<string, string> previous)
    {
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var world in WorldData.Worlds.OrderBy(world => world.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.ContainsKey(world.DataCenter)) continue;
            if (eligible.TryGetValue(world.DataCenter, out var available))
                selected[world.DataCenter] = available;
            else if (previous.TryGetValue(world.DataCenter, out var saved)
                && !string.IsNullOrWhiteSpace(saved)
                && WorldData.GetByName(saved.Trim()) is { } known
                && known.DataCenter.Equals(world.DataCenter, StringComparison.OrdinalIgnoreCase))
                selected[world.DataCenter] = known.Name;
            else
                selected[world.DataCenter] = world.Name;
        }
        return selected;
    }

    internal static AethertekWorldSelection Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ReadInteger(root, "schema_version") != 1)
            throw new InvalidDataException("The server status format is not supported.");
        var staleAfterSeconds = ReadInteger(root, "stale_after_seconds");
        if (staleAfterSeconds < 30 || staleAfterSeconds > 10800)
            throw new InvalidDataException("The server status expiry is invalid.");
        if (!root.TryGetProperty("worlds", out var section) || section.ValueKind != JsonValueKind.Object
            || !ReadString(section, "status").Equals("available", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("World status is currently unavailable.");
        var checkedAt = ReadInteger(section, "checked_at");
        if (!IsFresh(ReadInteger(root, "generated_at"), now.ToUnixTimeSeconds(), staleAfterSeconds)
            || !IsFresh(checkedAt, now.ToUnixTimeSeconds(), staleAfterSeconds))
            throw new InvalidDataException("The server status is stale. Try again after the website refreshes.");
        if (!section.TryGetProperty("worlds", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The world list is missing.");

        // Only current world rows are read. History, probes and service summaries do not
        // select destinations. Validate against the same world map used by Xagman travel.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worlds = new List<AethertekWorldStatus>();
        foreach (var row in rows.EnumerateArray())
        {
            var name = ReadString(row, "name");
            var known = WorldData.GetByName(name);
            if (known == null) continue;
            var region = ReadString(row, "region");
            if (region.Equals("OC", StringComparison.OrdinalIgnoreCase)) region = "OCE";
            if (!known.DataCenter.Equals(ReadString(row, "dc"), StringComparison.OrdinalIgnoreCase)
                || !known.Region.Equals(region, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(known.Name))
                throw new InvalidDataException("The world list contains duplicate or mismatched world details.");
            var creationAllowed = row.TryGetProperty("creation", out var creation) && creation.ValueKind == JsonValueKind.True;
            worlds.Add(new AethertekWorldStatus(known.Name, known.DataCenter,
                ReadString(row, "status"), ReadString(row, "category"), creationAllowed));
        }
        if (WorldData.Worlds.Any(world => !seen.Contains(world.Name)))
            throw new InvalidDataException("The world list is incomplete. Existing selections were kept.");

        var selected = SelectWorlds(worlds);
        var unavailable = WorldData.RegionOrder.SelectMany(region => WorldData.DataCenterOrder[region])
            .Where(dc => !selected.ContainsKey(dc)).ToList();
        return new AethertekWorldSelection(selected, unavailable, DateTimeOffset.FromUnixTimeSeconds(checkedAt));
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The server status is missing '{name}'.");
        return value.GetString()!.Trim();
    }

    private static long ReadInteger(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidDataException($"The server status is missing '{name}'.");
        return number;
    }
}
