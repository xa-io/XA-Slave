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

public enum XagmanRunPhase
{
    Legacy = 0,
    Collection = 1,
    Restock = 2,
}

public enum XagmanItemMode
{
    Give = 0,
    Take = 1,
    Balance = 2,
    TopUp = 3,
}

public enum XagmanItemSelectorKind
{
    ExactItem = 0,
    GreenItemGcSeals = 1,
    GreenItemFcCreditsRankProgress = 2,
}

public enum XagmanItemApplicability
{
    All = 0,
    HasRetainers = 1,
    HasSubmarines = 2,
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
    public XagmanItemSelectorKind SelectorKind { get; set; } = XagmanItemSelectorKind.ExactItem;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public XagmanItemMode Mode { get; set; } = XagmanItemMode.Give;
    public XagmanItemApplicability Applicability { get; set; } = XagmanItemApplicability.All;
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
    public XagmanItemSelectorKind SelectorKind { get; set; } = XagmanItemSelectorKind.ExactItem;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public XagmanItemMode Mode { get; set; }
    public int Quantity { get; set; }
    public int TargetQuantity { get; set; }
    public int CurrentQuantity { get; set; }
    public int GreenValueProtocolRevision { get; set; }
    public long TargetValueScaled2 { get; set; }
    public long CurrentValueScaled2 { get; set; }
    public long ValueDeficitScaled2 { get; set; }
    public bool GreenScanComplete { get; set; } = true;
    public string GreenScanError { get; set; } = string.Empty;
}

public sealed class XagmanGreenValueSnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public int Revision { get; set; }
    public bool Complete { get; set; }
    public string Error { get; set; } = string.Empty;
    public long GcSealsScaled2 { get; set; }
    public long FcCreditsScaled2 { get; set; }
    public long GcSealsTargetScaled2 { get; set; }
    public long FcCreditsTargetScaled2 { get; set; }
    public long DropboxGcSealsScaled2 { get; set; }
    public long DropboxFcCreditsScaled2 { get; set; }
    public int SafeItemCount { get; set; }
    public int DropboxSafeItemCount { get; set; }
    public int ExcludedItemCount { get; set; }
    public int BlockedKeyCount { get; set; }
}

public sealed class XagmanTradeCapacityForecast
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Revision { get; set; } = string.Empty;
    public int SelectedOwnerCount { get; set; }
    public int KnownOwnerCount { get; set; }
    public int UnknownOwnerCount { get; set; }
    public bool IsTruncated { get; set; }
    public List<string> SelectedOwnerKeys { get; set; } = new();
    public List<XagmanTradeCapacityForecastItem> Items { get; set; } = new();
}

public sealed class XagmanTradeCapacityForecastItem
{
    // Region for Server Matching, or the fixed aggregation source used by a fixed-world run.
    public string GroupKey { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public int StackSize { get; set; }
    public long IncomingToTonyQuantity { get; set; }
    public long NeededFromTonyQuantity { get; set; }
    public int AllAvailableRequestCount { get; set; }
    public int KnownOwnerCount { get; set; }
    public int UnknownOwnerCount { get; set; }
}

public sealed class XagmanPeerPresence
{
    public string InstanceId { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
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
    public int CoordinationProtocolRevision { get; set; }
    public int GreenValueProtocolRevision { get; set; }
    public string RunId { get; set; } = string.Empty;
    public bool CollectionFirstEnabled { get; set; }
    public bool CollectionFirstRequested { get; set; }
    public bool HasConditionalItemPolicies { get; set; }
    public XagmanRunPhase RunPhase { get; set; }
    public int PhaseTotalCharacters { get; set; }
    public int PhaseResolvedCharacters { get; set; }
    public bool PhaseComplete { get; set; }
    public bool CompletionDirectiveAcknowledged { get; set; }
    public string ActiveCharacter { get; set; } = string.Empty;
    public string PreferredTonyCharacter { get; set; } = string.Empty;
    public string MeetWorld { get; set; } = string.Empty;
    public string MeetAetheryte { get; set; } = string.Empty;
    // Server Matching: Tony advertises the server (data center) it is currently sweeping.
    // FOs engage only when their character's server matches; they wait (logged out) when the
    // sweep ordinal is below theirs, and treat themselves as skipped once it passes them.
    public bool ServerMatchingEnabled { get; set; }
    public int ServerMatchingSweepOrdinal { get; set; } = -1;
    public string ServerMatchingActiveDataCenter { get; set; } = string.Empty;
    // FO advertises the server (data center) of the character it is currently processing or waiting on,
    // so the sweeping Tony can tell when a server still has owners pending versus drained.
    public string ServerMatchingPendingDataCenter { get; set; } = string.Empty;
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
    public XagmanGreenValueSnapshot? GreenValueSnapshot { get; set; }
    public XagmanTradeCapacityForecast? TradeCapacityForecast { get; set; }
}

public sealed class XagmanPeerMessage
{
    public string MessageType { get; set; } = string.Empty;
    public string SenderInstanceId { get; set; } = string.Empty;
    public string TargetInstanceId { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
    // Optional stop-task behavior. Keeping the established message type lets older clients
    // still stop safely even though they cannot perform the newer result clear.
    public bool ClearResults { get; set; }
    public int CoordinationProtocolRevision { get; set; }
    public int GreenValueProtocolRevision { get; set; }
    public string RunId { get; set; } = string.Empty;
    public bool CollectionFirstEnabled { get; set; }
    public XagmanRunPhase RunPhase { get; set; }
    public List<string> ExpectedFranchiseOwnerInstanceIds { get; set; } = new();
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
