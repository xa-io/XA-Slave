using System;
using System.Collections.Generic;

namespace XASlave.Data;

public enum XagmanRole
{
    Tony,
    FranchiseOwner,
}

public enum XagmanTonyMode
{
    Resupply,
    Collection,
}

public enum XagmanItemMode
{
    Give = 0,
    Take = 1,
    Balance = 2,
    TopUp = 3,
}

public enum XagmanStatus
{
    Idle,
    Preflight,
    Relogging,
    Traveling,
    AtMeetSpot,
    ReadyForQueue,
    WaitingRoom,
    Queued,
    Called,
    Trading,
    Standby,
    ReturningHome,
    Completed,
    Paused,
    Error,
}

[Serializable]
public sealed class XagmanItemEntry
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public XagmanItemMode Mode { get; set; } = XagmanItemMode.Give;
    public int Quantity { get; set; } = 1;
}

[Serializable]
public sealed class XagmanNamedItemList
{
    public string Name { get; set; } = string.Empty;
    public List<XagmanItemEntry> Items { get; set; } = new();
}

[Serializable]
public sealed class XagmanTonyCharacterEntry
{
    public string CharacterNameWorld { get; set; } = string.Empty;
    public XagmanTonyMode Mode { get; set; } = XagmanTonyMode.Collection;
}

public sealed class XagmanTradeRequestEntry
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public XagmanItemMode Mode { get; set; }
    public int Quantity { get; set; }
    public int TargetQuantity { get; set; }
    public int CurrentQuantity { get; set; }
}

public sealed class XagmanPeerPresence
{
    public string InstanceId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsLoggedIn { get; set; }
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string HomeWorld { get; set; } = string.Empty;
    public string CurrentWorld { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    public bool LocalPositionAvailable { get; set; }
    public float LocalPositionX { get; set; }
    public float LocalPositionY { get; set; }
    public float LocalPositionZ { get; set; }
    public bool XagmanEnabled { get; set; }
    public XagmanRole Role { get; set; }
    public XagmanTonyMode TonyMode { get; set; }
    public XagmanStatus Status { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string ActiveCharacter { get; set; } = string.Empty;
    public string PreferredTonyCharacter { get; set; } = string.Empty;
    public string MeetWorld { get; set; } = string.Empty;
    public string MeetAetheryte { get; set; } = string.Empty;
    public DateTime QueueRequestedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime TonyCompletionRequestedAtUtc { get; set; } = DateTime.MinValue;
    public int TotalCharacters { get; set; }
    public int CompletedCharacters { get; set; }
    public int QueueNumber { get; set; }
    public string ActiveTradePartner { get; set; } = string.Empty;
    public string ActiveTradePartnerInstanceId { get; set; } = string.Empty;
    public bool TonyRotationReady { get; set; }
    public int MainInventoryFreeSlots { get; set; }
    public int Gil { get; set; }
    public int TonyGilMinimum { get; set; } = -1;
    public bool TonySellLocationActive { get; set; }
    public uint TonySellLocationTerritoryId { get; set; }
    public string TonySellLocationName { get; set; } = string.Empty;
    public float TonySellLocationX { get; set; }
    public float TonySellLocationY { get; set; }
    public float TonySellLocationZ { get; set; }
    public List<uint> ItemIds { get; set; } = new();
    public List<XagmanTradeRequestEntry> RequestedItems { get; set; } = new();
}

public sealed class XagmanPeerMessage
{
    public string MessageType { get; set; } = string.Empty;
    public string SenderInstanceId { get; set; } = string.Empty;
    public string TargetInstanceId { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
    public XagmanPeerPresence? Presence { get; set; }
    public List<XagmanPeerPresence> Peers { get; set; } = new();
}

public static class XagmanPeerMessageTypes
{
    public const string Register = "register";
    public const string PeerList = "peer-list";
    public const string StartTask = "start-task";
    public const string StopTask = "stop-task";
    public const string RecallTask = "recall-task";
    public const string CompleteTask = "complete-task";
}
