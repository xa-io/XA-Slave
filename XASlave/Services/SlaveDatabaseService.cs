using System;
using System.Globalization;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Data.Sqlite;

namespace XASlave.Services;

public sealed class SlaveDatabaseService
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly IPluginLog log;
    private readonly string dbPath;

    public string DbPath => dbPath;

    public SlaveDatabaseService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        var configDir = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(configDir);
        dbPath = Path.Combine(configDir, "slave.db");

        InitializeSchema();
    }

    public DateTime? GetLastSyncedToXaDbUtc(ulong contentId)
    {
        if (contentId == 0)
            return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_synced_to_xa_db FROM xa_sync_state WHERE content_id = @cid";
            cmd.Parameters.AddWithValue("@cid", (long)contentId);

            var result = cmd.ExecuteScalar()?.ToString();
            return ParseUtc(result);
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] Slave DB read failed: {ex.Message}");
            return null;
        }
    }

    public void RecordLastSyncedToXaDb(ulong contentId, string characterName, DateTime? syncedAtUtc = null)
    {
        if (contentId == 0)
            return;

        var stamp = (syncedAtUtc ?? DateTime.UtcNow).ToString(TimestampFormat, CultureInfo.InvariantCulture);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO xa_sync_state (content_id, character_name, last_synced_to_xa_db)
                VALUES (@cid, @name, @stamp)
                ON CONFLICT(content_id) DO UPDATE SET
                    character_name = excluded.character_name,
                    last_synced_to_xa_db = excluded.last_synced_to_xa_db";
            cmd.Parameters.AddWithValue("@cid", (long)contentId);
            cmd.Parameters.AddWithValue("@name", characterName ?? string.Empty);
            cmd.Parameters.AddWithValue("@stamp", stamp);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] Slave DB write failed: {ex.Message}");
        }
    }

    public bool IsSyncDue(ulong contentId, int everyHours)
    {
        if (everyHours <= 0 || contentId == 0)
            return true;

        var lastSync = GetLastSyncedToXaDbUtc(contentId);
        if (!lastSync.HasValue)
            return true;

        return DateTime.UtcNow - lastSync.Value >= TimeSpan.FromHours(everyHours);
    }

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS xa_sync_state (
                content_id INTEGER PRIMARY KEY,
                character_name TEXT NOT NULL DEFAULT '',
                last_synced_to_xa_db TEXT NOT NULL DEFAULT ''
            )";
        cmd.ExecuteNonQuery();
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            return parsed;

        return null;
    }
}
