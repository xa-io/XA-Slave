using System;
using System.Collections.Generic;

namespace XASlave.Services;

/// <summary>
/// Owns the mutable outcome state for one task run and exposes detached snapshots only.
/// </summary>
internal sealed class TaskRunResults
{
    private readonly object gate = new();
    private readonly List<string> failedCharacters = new();
    private readonly HashSet<string> failedCharacterKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> incompleteCharacters = new();
    private readonly HashSet<string> incompleteCharacterKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> itemStartTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> itemDurations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FailedCharactersSnapshot()
    {
        lock (gate)
            return failedCharacters.ToArray();
    }

    public IReadOnlyList<string> IncompleteCharactersSnapshot()
    {
        lock (gate)
            return incompleteCharacters.ToArray();
    }

    public IReadOnlyDictionary<string, double> ItemDurationsSnapshot()
    {
        lock (gate)
            return new Dictionary<string, double>(itemDurations, StringComparer.OrdinalIgnoreCase);
    }

    public bool RecordFailedCharacter(string characterName)
    {
        var normalized = characterName?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return false;

        lock (gate)
        {
            if (!failedCharacterKeys.Add(normalized))
                return false;

            failedCharacters.Add(normalized);
            return true;
        }
    }

    public bool RecordIncompleteCharacter(string characterName)
    {
        var normalized = characterName?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return false;

        lock (gate)
        {
            if (!incompleteCharacterKeys.Add(normalized))
                return false;

            incompleteCharacters.Add(normalized);
            return true;
        }
    }

    public void RecordItemStart(string item, DateTime startedAtUtc)
    {
        var normalized = item?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return;

        lock (gate)
            itemStartTimes[normalized] = startedAtUtc;
    }

    public void RecordItemEnd(string item, DateTime finishedAtUtc)
    {
        var normalized = item?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return;

        lock (gate)
        {
            if (itemStartTimes.TryGetValue(normalized, out var start) && !itemDurations.ContainsKey(normalized))
                itemDurations[normalized] = Math.Max(0, (finishedAtUtc - start).TotalSeconds);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            failedCharacters.Clear();
            failedCharacterKeys.Clear();
            incompleteCharacters.Clear();
            incompleteCharacterKeys.Clear();
            itemStartTimes.Clear();
            itemDurations.Clear();
        }
    }
}
