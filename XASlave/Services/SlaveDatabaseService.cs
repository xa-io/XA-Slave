using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Data.Sqlite;
using XASlave.Data;

namespace XASlave.Services;

public sealed class SlaveDatabaseService
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const int XAPeepSessionHistoryLimit = 500;

    private readonly IPluginLog log;
    private readonly string dbPath;
    private readonly object schemaLock = new();
    private bool schemaInitialized;

    public string DbPath => dbPath;

    public SlaveDatabaseService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        var configDir = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(configDir);
        dbPath = Path.Combine(configDir, "slave.db");
    }

    public DateTime? GetLastSyncedToXaDbUtc(ulong contentId)
    {
        if (contentId == 0)
            return null;

        try
        {
            EnsureSchemaInitialized();
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
            EnsureSchemaInitialized();
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

    public void RecordXAPeepSession(string playerName, uint homeWorldId, uint jobId, uint territoryId, DateTime startedUtc, DateTime endedUtc)
    {
        var safeEndedUtc = endedUtc < startedUtc ? startedUtc : endedUtc;
        var durationSeconds = Math.Max(0d, (safeEndedUtc - startedUtc).TotalSeconds);
        var playerKey = XAPeepData.NormalizePlayerKey(playerName, homeWorldId);
        var displayName = XAPeepData.FormatDisplayName(playerName, homeWorldId);
        var startedStamp = startedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var endedStamp = safeEndedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        try
        {
            EnsureSchemaInitialized();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var tx = conn.BeginTransaction();

            using (var sessionCmd = conn.CreateCommand())
            {
                sessionCmd.Transaction = tx;
                sessionCmd.CommandText = @"
                    INSERT INTO xa_peep_sessions (
                        player_key,
                        display_name,
                        home_world_id,
                        job_id,
                        territory_id,
                        started_utc,
                        ended_utc,
                        duration_seconds)
                    VALUES (
                        @playerKey,
                        @displayName,
                        @homeWorldId,
                        @jobId,
                        @territoryId,
                        @startedUtc,
                        @endedUtc,
                        @durationSeconds)";
                sessionCmd.Parameters.AddWithValue("@playerKey", playerKey);
                sessionCmd.Parameters.AddWithValue("@displayName", displayName);
                sessionCmd.Parameters.AddWithValue("@homeWorldId", (long)homeWorldId);
                sessionCmd.Parameters.AddWithValue("@jobId", (long)jobId);
                sessionCmd.Parameters.AddWithValue("@territoryId", (long)territoryId);
                sessionCmd.Parameters.AddWithValue("@startedUtc", startedStamp);
                sessionCmd.Parameters.AddWithValue("@endedUtc", endedStamp);
                sessionCmd.Parameters.AddWithValue("@durationSeconds", durationSeconds);
                sessionCmd.ExecuteNonQuery();
            }

            using (var summaryCmd = conn.CreateCommand())
            {
                summaryCmd.Transaction = tx;
                summaryCmd.CommandText = @"
                    INSERT INTO xa_peep_players (
                        player_key,
                        display_name,
                        home_world_id,
                        last_job_id,
                        first_targeted_utc,
                        last_targeted_utc,
                        total_target_count,
                        total_target_duration_seconds,
                        last_territory_id)
                    VALUES (
                        @playerKey,
                        @displayName,
                        @homeWorldId,
                        @jobId,
                        @startedUtc,
                        @endedUtc,
                        1,
                        @durationSeconds,
                        @territoryId)
                    ON CONFLICT(player_key) DO UPDATE SET
                        display_name = excluded.display_name,
                        home_world_id = excluded.home_world_id,
                        last_job_id = CASE
                            WHEN excluded.last_job_id > 0 THEN excluded.last_job_id
                            ELSE xa_peep_players.last_job_id
                        END,
                        first_targeted_utc = CASE
                            WHEN xa_peep_players.first_targeted_utc = '' THEN excluded.first_targeted_utc
                            WHEN excluded.first_targeted_utc = '' THEN xa_peep_players.first_targeted_utc
                            WHEN xa_peep_players.first_targeted_utc <= excluded.first_targeted_utc THEN xa_peep_players.first_targeted_utc
                            ELSE excluded.first_targeted_utc
                        END,
                        last_targeted_utc = excluded.last_targeted_utc,
                        total_target_count = xa_peep_players.total_target_count + 1,
                        total_target_duration_seconds = xa_peep_players.total_target_duration_seconds + excluded.total_target_duration_seconds,
                        last_territory_id = excluded.last_territory_id";
                summaryCmd.Parameters.AddWithValue("@playerKey", playerKey);
                summaryCmd.Parameters.AddWithValue("@displayName", displayName);
                summaryCmd.Parameters.AddWithValue("@homeWorldId", (long)homeWorldId);
                summaryCmd.Parameters.AddWithValue("@jobId", (long)jobId);
                summaryCmd.Parameters.AddWithValue("@startedUtc", startedStamp);
                summaryCmd.Parameters.AddWithValue("@endedUtc", endedStamp);
                summaryCmd.Parameters.AddWithValue("@durationSeconds", durationSeconds);
                summaryCmd.Parameters.AddWithValue("@territoryId", (long)territoryId);
                summaryCmd.ExecuteNonQuery();
            }

            using (var trimCmd = conn.CreateCommand())
            {
                trimCmd.Transaction = tx;
                trimCmd.CommandText = @"
                    DELETE FROM xa_peep_sessions
                    WHERE id NOT IN (
                        SELECT id
                        FROM xa_peep_sessions
                        ORDER BY ended_utc DESC, id DESC
                        LIMIT @keepCount)";
                trimCmd.Parameters.AddWithValue("@keepCount", XAPeepSessionHistoryLimit);
                trimCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] XA Peep write failed: {ex.Message}");
        }
    }

    public bool TryGetXAPeepPlayerSummary(string playerKey, out XAPeepPlayerSummary? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(playerKey))
            return false;

        try
        {
            EnsureSchemaInitialized();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    player_key,
                    display_name,
                    home_world_id,
                    last_job_id,
                    total_target_count,
                    total_target_duration_seconds,
                    first_targeted_utc,
                    last_targeted_utc,
                    last_territory_id
                FROM xa_peep_players
                WHERE player_key = @playerKey
                LIMIT 1";
            cmd.Parameters.AddWithValue("@playerKey", playerKey);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;

            summary = ReadXAPeepPlayerSummary(reader);
            return summary != null;
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] XA Peep summary lookup failed: {ex.Message}");
            return false;
        }
    }

    public List<XAPeepPlayerSummary> GetRecentXAPeepPlayers(int limit)
    {
        var summaries = new List<XAPeepPlayerSummary>();
        limit = Math.Clamp(limit, 1, 500);

        try
        {
            EnsureSchemaInitialized();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    player_key,
                    display_name,
                    home_world_id,
                    last_job_id,
                    total_target_count,
                    total_target_duration_seconds,
                    first_targeted_utc,
                    last_targeted_utc,
                    last_territory_id
                FROM xa_peep_players
                ORDER BY last_targeted_utc DESC, total_target_count DESC, display_name ASC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var summary = ReadXAPeepPlayerSummary(reader);
                if (summary != null)
                    summaries.Add(summary);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] XA Peep players query failed: {ex.Message}");
        }

        return summaries;
    }

    public List<XAPeepSessionRecord> GetRecentXAPeepSessions(int limit)
    {
        var sessions = new List<XAPeepSessionRecord>();
        limit = Math.Clamp(limit, 1, 500);

        try
        {
            EnsureSchemaInitialized();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    id,
                    player_key,
                    display_name,
                    home_world_id,
                    job_id,
                    territory_id,
                    started_utc,
                    ended_utc,
                    duration_seconds
                FROM xa_peep_sessions
                ORDER BY ended_utc DESC, id DESC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new XAPeepSessionRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadUInt(reader, 3),
                    ReadUInt(reader, 4),
                    ReadUInt(reader, 5),
                    ParseUtc(reader.GetString(6)) ?? DateTime.UtcNow,
                    ParseUtc(reader.GetString(7)) ?? DateTime.UtcNow,
                    reader.GetDouble(8)));
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] XA Peep sessions query failed: {ex.Message}");
        }

        return sessions;
    }

    public void ClearXAPeepHistory()
    {
        try
        {
            EnsureSchemaInitialized();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var tx = conn.BeginTransaction();

            using (var deleteSessions = conn.CreateCommand())
            {
                deleteSessions.Transaction = tx;
                deleteSessions.CommandText = "DELETE FROM xa_peep_sessions";
                deleteSessions.ExecuteNonQuery();
            }

            using (var deletePlayers = conn.CreateCommand())
            {
                deletePlayers.Transaction = tx;
                deletePlayers.CommandText = "DELETE FROM xa_peep_players";
                deletePlayers.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] XA Peep clear-history failed: {ex.Message}");
        }
    }

    private void EnsureSchemaInitialized()
    {
        if (schemaInitialized)
            return;

        lock (schemaLock)
        {
            if (schemaInitialized)
                return;

            InitializeSchema();
            schemaInitialized = true;
        }
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
            );

            CREATE TABLE IF NOT EXISTS xa_peep_players (
                player_key TEXT PRIMARY KEY,
                display_name TEXT NOT NULL DEFAULT '',
                home_world_id INTEGER NOT NULL DEFAULT 0,
                last_job_id INTEGER NOT NULL DEFAULT 0,
                first_targeted_utc TEXT NOT NULL DEFAULT '',
                last_targeted_utc TEXT NOT NULL DEFAULT '',
                total_target_count INTEGER NOT NULL DEFAULT 0,
                total_target_duration_seconds REAL NOT NULL DEFAULT 0,
                last_territory_id INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS xa_peep_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                player_key TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                home_world_id INTEGER NOT NULL DEFAULT 0,
                job_id INTEGER NOT NULL DEFAULT 0,
                territory_id INTEGER NOT NULL DEFAULT 0,
                started_utc TEXT NOT NULL DEFAULT '',
                ended_utc TEXT NOT NULL DEFAULT '',
                duration_seconds REAL NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_xa_peep_players_last_targeted
                ON xa_peep_players(last_targeted_utc DESC);

            CREATE INDEX IF NOT EXISTS idx_xa_peep_sessions_ended
                ON xa_peep_sessions(ended_utc DESC, id DESC);

            CREATE INDEX IF NOT EXISTS idx_xa_peep_sessions_player_key
                ON xa_peep_sessions(player_key);";
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

    private static uint ReadUInt(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        var value = reader.GetInt64(ordinal);
        return value < 0
            ? 0
            : (uint)Math.Min(value, uint.MaxValue);
    }

    private static XAPeepPlayerSummary? ReadXAPeepPlayerSummary(SqliteDataReader reader)
    {
        if (reader == null)
            return null;

        var playerKey = reader.GetString(0);
        var displayName = reader.GetString(1);
        var homeWorldId = ReadUInt(reader, 2);
        var jobId = ReadUInt(reader, 3);
        var totalTargetCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
        var totalTargetDurationSeconds = reader.IsDBNull(5) ? 0d : reader.GetDouble(5);
        var firstTargetedUtc = ParseUtc(reader.IsDBNull(6) ? null : reader.GetString(6)) ?? DateTime.MinValue;
        var lastTargetedUtc = ParseUtc(reader.IsDBNull(7) ? null : reader.GetString(7)) ?? DateTime.MinValue;
        var lastTerritoryId = ReadUInt(reader, 8);

        return new XAPeepPlayerSummary(
            playerKey,
            displayName,
            homeWorldId,
            jobId,
            totalTargetCount,
            totalTargetDurationSeconds,
            firstTargetedUtc,
            lastTargetedUtc,
            lastTerritoryId);
    }
}
