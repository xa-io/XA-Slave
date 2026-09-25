using System;
using System.Collections.Generic;

namespace XASlave.Data;

// Local configuration data only: never retain native pointers or entity IDs.
[Serializable]
public sealed class InspectOutfitHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlayerName { get; set; } = string.Empty;
    public ushort WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public ushort FacewearId { get; set; }
    public List<InspectOutfitHistoryItem> Equipment { get; set; } = new();
}

[Serializable]
public sealed class InspectOutfitHistoryItem
{
    public ushort Slot { get; set; }
    public uint BaseId { get; set; }
    public uint GlamourId { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}
