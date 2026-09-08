using System;
using System.Collections.Generic;

namespace XASlave.Services;

internal sealed class BoundedLogBuffer
{
    private readonly object syncRoot = new();
    private readonly Queue<string> entries;
    private readonly int capacity;
    private long evictedEntryCount;

    public BoundedLogBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
        entries = new Queue<string>(capacity);
    }

    public void Add(string message)
    {
        lock (syncRoot)
        {
            if (entries.Count >= capacity)
            {
                entries.Dequeue();
                evictedEntryCount++;
            }

            entries.Enqueue(message);
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            entries.Clear();
            evictedEntryCount = 0;
        }
    }

    public IReadOnlyList<string> Snapshot(bool includeEvictionNotice = false)
    {
        lock (syncRoot)
        {
            if (includeEvictionNotice && evictedEntryCount > 0)
            {
                // The notice is synthetic so it never evicts a retained entry or affects capacity.
                var snapshot = new string[entries.Count + 1];
                snapshot[0] = $"[Log history] {evictedEntryCount} earlier entries omitted after reaching the {capacity}-entry limit.";
                entries.CopyTo(snapshot, 1);
                return snapshot;
            }

            return entries.ToArray();
        }
    }
}
