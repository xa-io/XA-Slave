using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace XASlave.Services;

internal sealed record ViewSingleItemHistoryEntry(long Id, string PlayerName, ushort World,
    DateTime CapturedUtc, ViewSingleItemCapture? Capture, string Error)
{
    private sealed record Presentation(global::XASlave.Localization.UiMessage? ErrorMessage, global::XASlave.Localization.UiMessage? NameMessage);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewSingleItemHistoryEntry, Presentation> Presentations = new();
    private static readonly Presentation EmptyPresentation = new(null, null);
    private Presentation Messages => Presentations.TryGetValue(this, out var value) ? value : EmptyPresentation;
    internal global::XASlave.Localization.UiMessage? ErrorMessage
    {
        get => Messages.ErrorMessage;
        init => Presentations.AddOrUpdate(this, Messages with { ErrorMessage = value });
    }
    internal global::XASlave.Localization.UiMessage? NameMessage
    {
        get => Messages.NameMessage;
        init => Presentations.AddOrUpdate(this, Messages with { NameMessage = value });
    }
    private ViewSingleItemHistoryEntry(ViewSingleItemHistoryEntry original)
    {
        Id = original.Id;
        PlayerName = original.PlayerName;
        World = original.World;
        CapturedUtc = original.CapturedUtc;
        Capture = original.Capture;
        Error = original.Error;
        if (Presentations.TryGetValue(original, out var presentation)) Presentations.Add(this, presentation);
    }
    internal global::XASlave.Localization.UiMessage ErrorPresentation => global::XASlave.Localization.UiMessage.Capture(ErrorMessage, Error);
    internal string LocalizedError => ErrorPresentation.Display;
    internal string LocalizedPlayerName => global::XASlave.Localization.UiMessage.Resolve(NameMessage, PlayerName);
}

// Serialized background I/O: Draw reads an immutable list and never opens SQLite.
// Each operation owns its connection; no worker touches game or plugin services.
internal sealed class ViewSingleItemHistory : IDisposable
{
    private readonly string connectionString;
    private readonly object sync = new();
    private Task pending = Task.CompletedTask;
    private int queued;
    private bool disposed;
    private ViewSingleItemHistoryEntry[] entries = Array.Empty<ViewSingleItemHistoryEntry>();
    private string status = global::XASlave.Localization.UiLocale.Native("SF295265E61972A9B");

    internal ViewSingleItemHistory(string path)
    {
        connectionString = new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString();
    }

    internal IReadOnlyList<ViewSingleItemHistoryEntry> Entries => Volatile.Read(ref entries);
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("SF295265E61972A9B");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, status);
    internal string LocalizedStatus => StatusMessage.Display;
    internal string Status => Volatile.Read(ref status);
    internal void ReportFailure(Exception error) => Volatile.Write(ref status, global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("SD38FE60DECA3EA65" , global::XASlave.Localization.UiMessage.FromException(error))));

    internal void Load() => Enqueue(null);

    internal void Save(ViewSingleItemCapture capture)
    {
        capture.Validate();
        // Serialize before queuing so no mutable arrays are shared with a worker.
        Enqueue(JsonSerializer.Serialize(capture));
    }

    private void Enqueue(string? captureJson)
    {
        lock (sync)
        {
            if (disposed) return;
            if (queued >= 8)
            { Volatile.Write(ref status, global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SDB204E0437A2E0C0"))); return; }
            queued++;
            Volatile.Write(ref status, global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S4FBA1D4B747601FE")));
            pending = pending.ContinueWith(_ =>
            {
                try { Process(captureJson); }
                catch (Exception error)
                { Volatile.Write(ref status, global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S62F2F0E1363D374F" , global::XASlave.Localization.UiMessage.FromException(error)))); }
                finally { lock (sync) queued--; }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Process(string? captureJson)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS view_single_item_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    player_name TEXT NOT NULL,
                    world INTEGER NOT NULL,
                    captured_utc TEXT NOT NULL,
                    capture_json TEXT NOT NULL CHECK(length(capture_json) <= 32768)
                );
                """;
            schema.ExecuteNonQuery();
        }
        if (captureJson != null)
        {
            if (captureJson.Length > 32768) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S44DA0EBAB854E7BF"), originalMessageText => new InvalidOperationException(originalMessageText));
            var capture = JsonSerializer.Deserialize<ViewSingleItemCapture>(captureJson)
                ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S8EFF2C9324FAAA68"), originalMessageText => new InvalidOperationException(originalMessageText));
            capture.Validate();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO view_single_item_history(player_name, world, captured_utc, capture_json)
                VALUES ($name, $world, $utc, $capture);
                DELETE FROM view_single_item_history WHERE id NOT IN
                    (SELECT id FROM view_single_item_history ORDER BY id DESC LIMIT 100);
                """;
            insert.Parameters.AddWithValue("$name", capture.PlayerName);
            insert.Parameters.AddWithValue("$world", (int)capture.World);
            insert.Parameters.AddWithValue("$utc", capture.CapturedUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$capture", captureJson);
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT id, substr(player_name, 1, 512), world, substr(captured_utc, 1, 64),
                CASE WHEN length(capture_json) <= 32768 THEN capture_json ELSE '' END
            FROM view_single_item_history ORDER BY id DESC LIMIT 100;
            """;
        using var reader = read.ExecuteReader();
        var result = new List<ViewSingleItemHistoryEntry>();
        while (reader.Read())
        {
            var nameMessage = global::XASlave.Localization.UiMessage.Key("SB28344DBEC2C16EA");
            var name = nameMessage.Original;
            global::XASlave.Localization.UiMessage? errorMessage = null;
            long world = 0;
            var time = DateTime.MinValue;
            ViewSingleItemCapture? capture = null;
            var error = string.Empty;
            try
            {
                name = reader.GetString(1);
                world = reader.GetInt64(2);
                if (!DateTime.TryParse(reader.GetString(3), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out time))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5C6368600BBD6B44"), originalMessageText => new InvalidOperationException(originalMessageText));
                capture = JsonSerializer.Deserialize<ViewSingleItemCapture>(reader.GetString(4));
                if (capture == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7DA95E7C8795AA23"), originalMessageText => new InvalidOperationException(originalMessageText));
                capture.Validate();
                if (capture.PlayerName != name || capture.World != world || capture.CapturedUtc != time)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2DA3D5CF04599CFA"), originalMessageText => new InvalidOperationException(originalMessageText));
            }
            catch (Exception failure) { capture = null; error = global::XASlave.Localization.UiMessage.Store(ref errorMessage, global::XASlave.Localization.UiMessage.FromException(failure)); }
            result.Add(new(reader.GetInt64(0), name, world is > 0 and <= ushort.MaxValue ? (ushort)world : (ushort)0,
                time, capture, error) { ErrorMessage = errorMessage, NameMessage = nameMessage });
        }
        Volatile.Write(ref entries, result.ToArray());
        Volatile.Write(ref status, global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S43FDEAD2CAAF33E3" , result.Count)));
    }

    public void Dispose()
    {
        // Already accepted writes drain without blocking the game thread. There
        // are no native references, plugin callbacks or pooled connections here.
        lock (sync) disposed = true;
    }
}
