using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace XASlave.Services;

internal sealed class XagmanTradeInventorySnapshot
{
    internal Dictionary<(uint ItemId, bool IsHq), long> Quantities { get; } = new();

    internal static bool TryRead(string json, ulong contentId, DateTime requestedAtUtc,
        out XagmanTradeInventorySnapshot snapshot)
    {
        snapshot = new();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || !root.GetProperty("complete").GetBoolean()
                || contentId == 0 || root.GetProperty("contentId").GetUInt64() != contentId
                || !root.GetProperty("capturedAtUtc").TryGetDateTime(out var capturedAtUtc)
                || capturedAtUtc.Kind != DateTimeKind.Utc || capturedAtUtc < requestedAtUtc
                || capturedAtUtc > DateTime.UtcNow.AddSeconds(1)) return false;
            var gil = root.GetProperty("gil").GetInt64();
            if (gil < 0 || gil > 999999999) return false;
            snapshot.Quantities.Add((1, false), gil);
            var items = root.GetProperty("items");
            if (items.GetArrayLength() > 4096) return false;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("itemId").GetUInt32();
                var hq = item.GetProperty("isHq").GetBoolean();
                var quantity = item.GetProperty("quantity").GetInt64();
                if (id <= 1 || quantity < 0 || !snapshot.Quantities.TryAdd((id, hq), quantity)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }
}

// The caller supplies the task's elapsed time, which already excludes explicit pauses.
internal sealed class XagmanTradeProgressMonitor
{
    internal const float ScanSeconds = 30;
    internal const int IdleScanLimit = 5;
    private XagmanTradeInventorySnapshot? baseline;
    private float previousElapsed;
    private float nextScanAt;
    private bool wasActive;
    private bool monitoringStarted;
    internal float ActiveSeconds { get; private set; }
    internal int IdleScans { get; private set; }
    internal int FailedScans { get; private set; }
    internal long ProgressSequence { get; private set; }
    internal long ScanSequence { get; private set; }
    internal bool Stalled => IdleScans >= IdleScanLimit;
    internal bool CaptureFailed => FailedScans >= IdleScanLimit;

    internal void Tick(float elapsed, bool active)
    {
        var delta = Math.Max(0, elapsed - previousElapsed);
        previousElapsed = elapsed;
        if (active && wasActive) ActiveSeconds += delta;
        if (!active)
        {
            baseline = null;
            IdleScans = FailedScans = 0;
            monitoringStarted = false;
            nextScanAt = ActiveSeconds;
        }
        wasActive = active;
    }

    internal bool ScanDue => wasActive && ActiveSeconds >= nextScanAt;

    internal void Observe(XagmanTradeInventorySnapshot? snapshot, float graceSeconds, bool outgoing,
        IReadOnlySet<(uint ItemId, bool IsHq)> watchedItems)
    {
        // A delayed frame produces one observation, never five catch-up scans.
        nextScanAt = ActiveSeconds + ScanSeconds;
        if (snapshot == null)
        {
            FailedScans++;
            return;
        }
        FailedScans = 0;
        ScanSequence++;
        var startsMonitoring = ActiveSeconds >= graceSeconds && !monitoringStarted;
        if (startsMonitoring)
        {
            monitoringStarted = true;
            IdleScans = 0;
        }
        if (baseline != null)
        {
            var changed = watchedItems.Any(key => outgoing
                ? snapshot.Quantities.GetValueOrDefault(key) < baseline.Quantities.GetValueOrDefault(key)
                : snapshot.Quantities.GetValueOrDefault(key) > baseline.Quantities.GetValueOrDefault(key));
            if (changed)
            {
                ProgressSequence++;
                IdleScans = 0;
            }
            else if (ActiveSeconds > graceSeconds && !startsMonitoring)
                IdleScans++;
        }
        baseline = snapshot;
    }
}
