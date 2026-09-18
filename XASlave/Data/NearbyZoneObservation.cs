using System;
using System.Collections.Generic;

namespace XASlave.Data;

public enum NearbyZoneMode { Search, ContentMember }

// Display-only cache copy. Content/account IDs and native pointers are deliberately omitted.
public sealed record NearbyZonePlayer(string Name, ushort HomeWorldId, byte JobId, ushort Location);

public sealed record NearbyZoneObservation(NearbyZoneMode Mode, long Generation, uint TerritoryId,
    DateTime ObservedUtc, int NativeEntryCount, IReadOnlyList<NearbyZonePlayer> Players)
{
    public bool PossiblyCapped => NativeEntryCount == 200;
    public int DisplayCount
    {
        get
        {
            if (Mode == NearbyZoneMode.ContentMember) return NativeEntryCount;
            var count = 0;
            foreach (var player in Players) if (player.JobId > 0) count++;
            return count;
        }
    }
}
