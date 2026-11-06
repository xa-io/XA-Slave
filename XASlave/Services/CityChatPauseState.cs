using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

/// <summary>Per-cycle pause intent and active time; no game or provider access.</summary>
internal sealed class CityChatPauseState
{
    private readonly Func<DateTime> utcNow;
    private TimeSpan pausedDuration;
    private DateTime pausedAt;
    internal bool PauseRequested { get; private set; }
    internal bool IsPaused { get; private set; }
    internal bool TravelCommitted { get; private set; }
    internal bool IsEnded { get; private set; }
    internal int ResumeRevision { get; private set; }
    internal DateTime ActiveUtcNow => (IsPaused ? pausedAt : utcNow()) - pausedDuration;

    internal CityChatPauseState(Func<DateTime>? clock = null) => utcNow = clock ?? (() => DateTime.UtcNow);

    internal void RequestPause() { if (!IsEnded) PauseRequested = true; }

    internal bool BeginPause()
    {
        if (IsEnded || !PauseRequested || TravelCommitted) return false;
        if (!IsPaused) { pausedAt = utcNow(); IsPaused = true; }
        return true;
    }

    internal void Resume()
    {
        if (IsEnded) return;
        if (IsPaused)
        {
            var elapsed = utcNow() - pausedAt;
            if (elapsed > TimeSpan.Zero) pausedDuration += elapsed;
            IsPaused = false;
            ResumeRevision++;
        }
        PauseRequested = false;
    }

    internal bool BeginTravel()
    {
        if (IsEnded || PauseRequested || IsPaused || TravelCommitted) return false;
        TravelCommitted = true;
        return true;
    }

    internal void CompleteTravel() => TravelCommitted = false;
    internal void End() { Resume(); IsEnded = true; TravelCommitted = false; PauseRequested = false; }
}

/// <summary>Detached immutable inputs retained across all cycles of one requested run.</summary>
internal sealed class CityChatRunSnapshot
{
    internal ulong ContentId { get; }
    internal IReadOnlyList<string> Worlds { get; }
    internal IReadOnlyList<CityChatDestination> Cities { get; }
    internal IReadOnlyList<string> Announcements { get; }
    internal string Channel { get; }
    internal float WaitBetweenCities { get; }
    internal float WaitAfterAnnounce { get; }
    internal bool Looping { get; }
    internal float LoopDelayMinutes { get; }

    internal CityChatRunSnapshot(ulong contentId, IEnumerable<string> worlds, IEnumerable<CityChatDestination> cities,
        IEnumerable<string> announcements, string channel, float waitBetweenCities, float waitAfterAnnounce, bool looping, float loopDelayMinutes)
    {
        ContentId = contentId;
        Worlds = Array.AsReadOnly(worlds.ToArray());
        Cities = Array.AsReadOnly(cities.ToArray());
        Announcements = Array.AsReadOnly(announcements.ToArray());
        Channel = channel;
        WaitBetweenCities = waitBetweenCities;
        WaitAfterAnnounce = waitAfterAnnounce;
        Looping = looping;
        LoopDelayMinutes = loopDelayMinutes;
    }
}
