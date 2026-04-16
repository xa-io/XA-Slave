using System;

namespace XASlave.Data;

public static class XAPeepData
{
    public const int MaxSoundEffectId = 16;

    public static string NormalizePlayerKey(string? name, uint homeWorldId)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        return $"{trimmedName.ToUpperInvariant()}@{homeWorldId}";
    }

    public static string FormatDisplayName(string? name, uint homeWorldId)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Unknown Player" : name.Trim();
        var worldName = WorldData.GetById(homeWorldId)?.Name;
        return string.IsNullOrWhiteSpace(worldName)
            ? trimmedName
            : $"{trimmedName}@{worldName}";
    }

    public static string FormatCompactName(string? displayName)
    {
        var trimmedName = string.IsNullOrWhiteSpace(displayName) ? "Unknown Player" : displayName.Trim();
        var atIndex = trimmedName.IndexOf('@');
        return atIndex > 0 ? trimmedName[..atIndex] : trimmedName;
    }

    public static int ClampSoundEffectId(int soundEffectId)
    {
        return Math.Clamp(soundEffectId, 0, MaxSoundEffectId);
    }

    public static uint GetSoundEffectValue(int soundEffectId)
    {
        var clamped = ClampSoundEffectId(soundEffectId);
        return clamped == 0 ? 0u : 0x24u + (uint)clamped;
    }

    public static string GetSoundEffectLabel(int soundEffectId)
    {
        var clamped = ClampSoundEffectId(soundEffectId);
        return clamped == 0 ? "None" : $"Alert {clamped}";
    }
}

public sealed record XAPeepPlayerSummary(
    string PlayerKey,
    string DisplayName,
    uint HomeWorldId,
    uint JobId,
    int TotalTargetCount,
    double TotalTargetDurationSeconds,
    DateTime FirstTargetedUtc,
    DateTime LastTargetedUtc,
    uint LastTerritoryId);

public sealed record XAPeepSessionRecord(
    long SessionId,
    string PlayerKey,
    string DisplayName,
    uint HomeWorldId,
    uint JobId,
    uint TerritoryId,
    DateTime StartedUtc,
    DateTime EndedUtc,
    double DurationSeconds);

public sealed record XAPeepLiveTargeterView(
    string PlayerKey,
    string DisplayName,
    string CompactName,
    uint HomeWorldId,
    uint JobId,
    ulong GameObjectId,
    DateTime StartedUtc,
    DateTime LastSeenUtc,
    TimeSpan CurrentDuration,
    int TotalTargetCount,
    double TotalTargetDurationSeconds);

public sealed record XAPeepTrackedPlayerView(
    string PlayerKey,
    string DisplayName,
    string CompactName,
    uint HomeWorldId,
    uint JobId,
    bool IsLive,
    ulong GameObjectId,
    DateTime FirstTargetedUtc,
    DateTime LastSeenUtc,
    DateTime CurrentStartedUtc,
    int TotalTargetCount,
    double TotalTargetDurationSeconds);
