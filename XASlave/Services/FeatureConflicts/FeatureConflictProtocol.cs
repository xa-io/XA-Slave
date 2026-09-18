using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace XA.FeatureConflicts;

// Source-shared protocol; plugins exchange only bounded JSON strings. Providers
// read PublishedJson, never game state, peer IPC, or a service lock.
internal sealed record FeatureConflictSnapshot(Guid Instance, long Generation, bool Ready, bool Enabled,
    bool ActiveOrBusy, bool Transitioning, bool RecoveryRequired)
{
    internal const int MaximumUtf8Bytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal bool Blocks => !Ready || Enabled || ActiveOrBusy || Transitioning || RecoveryRequired;

    internal string ToJson()
    {
        if (Instance == Guid.Empty || Generation < 0) throw new InvalidOperationException("Invalid conflict identity.");
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("instance", Instance.ToString("D"));
            writer.WriteNumber("generation", Generation);
            writer.WriteBoolean("ready", Ready);
            writer.WriteBoolean("enabled", Enabled);
            writer.WriteBoolean("activeOrBusy", ActiveOrBusy);
            writer.WriteBoolean("transitioning", Transitioning);
            writer.WriteBoolean("recoveryRequired", RecoveryRequired);
            writer.WriteEndObject();
        }
        return StrictUtf8.GetString(bytes.ToArray());
    }

    internal static bool TryParse(string? json, out FeatureConflictSnapshot? snapshot)
    {
        snapshot = null;
        if (json == null || json.Length > MaximumUtf8Bytes) return false;
        try
        {
            if (StrictUtf8.GetByteCount(json) > MaximumUtf8Bytes) return false;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) return false;
                if (property.Name is not ("schema" or "instance" or "generation" or "ready" or "enabled" or
                    "activeOrBusy" or "transitioning" or "recoveryRequired")) return false;
            }
            if (names.Count != 8 || !root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 ||
                !Guid.TryParseExact(root.GetProperty("instance").GetString(), "D", out var instance) || instance == Guid.Empty ||
                !root.GetProperty("generation").TryGetInt64(out var generation) || generation < 0) return false;
            snapshot = new FeatureConflictSnapshot(instance, generation, root.GetProperty("ready").GetBoolean(),
                root.GetProperty("enabled").GetBoolean(), root.GetProperty("activeOrBusy").GetBoolean(),
                root.GetProperty("transitioning").GetBoolean(), root.GetProperty("recoveryRequired").GetBoolean());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or EncoderFallbackException)
        {
            return false;
        }
    }
}

internal enum PeerPluginPhase { Unknown, Absent, Unloaded, Loading, Loaded, Unloading, Failed }
internal readonly record struct PeerPluginObservation(PeerPluginPhase Phase, Guid Installation);
internal readonly record struct FeatureConflictDecision(bool Permitted, string Reason)
{
    internal static FeatureConflictDecision Allow => new(true, "Ready");
    internal static FeatureConflictDecision Deny(string reason) => new(false, reason);
}

internal static class FeatureConflictPeer
{
    // Read each required endpoint twice and bracket it with fresh host lifecycle
    // observations. Call only from the framework's reconciliation/enable paths.
    internal static FeatureConflictDecision Read(Func<PeerPluginObservation> lifecycle, params Func<string?>[] providers)
    {
        try
        {
            var before = lifecycle();
            var absent = before.Phase is PeerPluginPhase.Absent or PeerPluginPhase.Unloaded;
            if (!absent && before.Phase != PeerPluginPhase.Loaded) return FeatureConflictDecision.Deny("Peer lifecycle is not ready");
            if (!absent && providers.Length == 0) return FeatureConflictDecision.Deny("Peer conflict providers are unavailable");
            var snapshots = new FeatureConflictSnapshot?[providers.Length];
            Guid? instance = null;
            for (var i = 0; i < providers.Length; ++i)
            {
                // Null means the IPC adapter positively observed no function.
                // Unloaded alone cannot erase a retained busy/recovery provider.
                var json = providers[i]();
                if (json == null && absent) continue;
                if (!FeatureConflictSnapshot.TryParse(json, out var state))
                    return FeatureConflictDecision.Deny("Peer conflict state is unavailable or incompatible");
                snapshots[i] = state!;
                if (state!.Blocks) return FeatureConflictDecision.Deny("Peer feature is enabled, busy, transitioning or requires recovery");
                if (instance.HasValue && state.Instance != instance.Value)
                    return FeatureConflictDecision.Deny("Peer providers have different plugin instances");
                instance = state.Instance;
            }
            for (var i = 0; i < providers.Length; ++i)
            {
                var json = providers[i]();
                if (json == null && absent && snapshots[i] == null) continue;
                if (!FeatureConflictSnapshot.TryParse(json, out var current) || current != snapshots[i])
                    return FeatureConflictDecision.Deny("Peer state changed during evaluation");
            }
            return before == lifecycle() ? FeatureConflictDecision.Allow : FeatureConflictDecision.Deny("Peer lifecycle changed");
        }
        catch { return FeatureConflictDecision.Deny("Peer conflict state could not be read"); }
    }
}

internal readonly record struct FeatureConflictActivity(bool Enabled, bool ActiveOrBusy, bool RecoveryRequired);

internal sealed class FeatureConflictPublication
{
    private readonly Func<bool> isFrameworkThread;
    private readonly Action<string>? publish;
    private readonly object publicationGate = new();
    private FeatureConflictSnapshot snapshot;
    private string publishedJson;
    private int transitioning;
    private bool shuttingDown;
    private bool publicationFailed;
    private string unavailableReason = "Conflict publication failed; reload is required after recovery";

    internal FeatureConflictPublication(Guid pluginInstance, Func<bool> isFrameworkThread, Action<string>? publish = null)
    {
        this.isFrameworkThread = isFrameworkThread;
        this.publish = publish;
        snapshot = new(pluginInstance, 0, false, false, false, false, false);
        publishedJson = snapshot.ToJson();
        publish?.Invoke(publishedJson);
    }

    internal string PublishedJson => Volatile.Read(ref publishedJson);
    internal string LastReason { get; private set; } = "Initializing";
    internal bool CanActivate
    {
        get { lock (publicationGate) return !publicationFailed && !shuttingDown && snapshot.Ready && !snapshot.RecoveryRequired; }
    }

    internal void MarkUnavailable(string reason)
    {
        lock (publicationGate)
        {
            publicationFailed = true;
            unavailableReason = reason;
            LastReason = reason;
        }
    }

    internal void MarkReady()
    {
        RequireFrameworkThread();
        lock (publicationGate)
        {
            if (shuttingDown || publicationFailed) return;
            Publish(snapshot with { Ready = true });
        }
    }

    internal void Refresh(FeatureConflictActivity actual)
    {
        lock (publicationGate)
            Publish(snapshot with { Enabled = actual.Enabled, ActiveOrBusy = actual.ActiveOrBusy, RecoveryRequired = actual.RecoveryRequired });
    }

    internal bool TryEnable(Func<FeatureConflictDecision> readPeer, Func<bool> enable, Func<FeatureConflictActivity> readActual)
    {
        if (!isFrameworkThread()) { LastReason = "Waiting for the framework thread"; return false; }
        if (Interlocked.CompareExchange(ref transitioning, 1, 0) != 0)
        { LastReason = "A feature transition is already in progress"; return false; }
        var applied = false;
        var actual = new FeatureConflictActivity(false, false, true);
        try
        {
            lock (publicationGate)
            {
                if (publicationFailed) { LastReason = unavailableReason; return false; }
                if (shuttingDown || !snapshot.Ready || snapshot.RecoveryRequired)
                { LastReason = "Feature is not ready or requires recovery"; return false; }
                Publish(snapshot with { Transitioning = true });
            }
            // No publication lock across peer calls or native feature work.
            var peer = readPeer();
            if (!peer.Permitted) { LastReason = peer.Reason; return false; }
            lock (publicationGate)
                if (shuttingDown) { LastReason = "Feature is shutting down"; return false; }
            applied = enable();
            LastReason = applied ? "Enabled" : "Feature could not enable";
        }
        catch { LastReason = "Feature transition failed"; }
        finally
        {
            try
            {
            try { actual = readActual(); Refresh(actual); }
            catch
            {
                lock (publicationGate) Publish(snapshot with { RecoveryRequired = true });
                LastReason = "Feature state is unknown; recovery is required";
            }
            lock (publicationGate) Publish(snapshot with { Transitioning = shuttingDown });
            }
            finally { Volatile.Write(ref transitioning, 0); }
        }
        return applied && actual.Enabled && !actual.RecoveryRequired && !shuttingDown;
    }

    // Publish BEFORE disabling hooks or sending restoration/exit commands.
    // Disabling and shutdown do not need permission from the peer.
    internal bool TryDisable(Action disable, Func<FeatureConflictActivity> readActual)
    {
        if (!isFrameworkThread()) { LastReason = "Waiting for the framework thread"; return false; }
        if (Interlocked.CompareExchange(ref transitioning, 1, 0) != 0) return false;
        var completed = false;
        var actual = new FeatureConflictActivity(true, false, true);
        try
        {
            // Failed IPC publication must never prevent local native cleanup.
            try { lock (publicationGate) Publish(snapshot with { Transitioning = true }); }
            catch { publicationFailed = true; }
            disable();
            completed = true;
        }
        catch { LastReason = "Feature cleanup failed"; }
        finally
        {
            try
            {
            try { actual = readActual(); Refresh(actual); }
            catch { completed = false; }
            lock (publicationGate)
                Publish(snapshot with { Transitioning = shuttingDown, RecoveryRequired = snapshot.RecoveryRequired || !completed });
            }
            finally { Volatile.Write(ref transitioning, 0); }
        }
        return completed && !actual.Enabled;
    }

    internal void BeginShutdown()
    {
        lock (publicationGate)
        {
            shuttingDown = true;
            Publish(snapshot with { Ready = false, Transitioning = true });
        }
    }

    // False means the transport must retain a blocking shutdown advertisement.
    // A cleared eligibility bit alone does not settle admitted native sends.
    internal bool CompleteShutdown(FeatureConflictActivity actual)
    {
        lock (publicationGate)
        {
            if (!shuttingDown) throw new InvalidOperationException("Shutdown has not begun.");
            var settled = Volatile.Read(ref transitioning) == 0 && !actual.Enabled && !actual.ActiveOrBusy && !actual.RecoveryRequired;
            Publish(snapshot with { Enabled = actual.Enabled, ActiveOrBusy = actual.ActiveOrBusy,
                RecoveryRequired = actual.RecoveryRequired, Transitioning = !settled });
            return settled;
        }
    }

    private void Publish(FeatureConflictSnapshot next)
    {
        if (next == snapshot) return;
        // Never wrap a generation or publish a misleading older value.
        next = next with { Generation = checked(snapshot.Generation + 1) };
        var json = next.ToJson();
        // Publish intent synchronously before any caller can apply native work.
        // A transport failure throws and the activation path fails closed.
        try { publish?.Invoke(json); }
        catch { publicationFailed = true; throw; }
        snapshot = next;
        Volatile.Write(ref publishedJson, json);
    }

    private void RequireFrameworkThread()
    {
        if (!isFrameworkThread()) throw new InvalidOperationException("Feature transitions require the framework thread.");
    }
}
