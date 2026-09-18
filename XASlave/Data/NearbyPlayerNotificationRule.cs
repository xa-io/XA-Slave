using System;
using System.Collections.Generic;

namespace XASlave.Data;

public enum NearbyPlayerNameMode { Any, Exact, Regex }
public enum NearbyPlayerTriggerMode { Appearance, Periodic }
public enum NearbyPlayerEntryMode { SlashCommand, ChatEntry }

// Persistence only. The scanner consumes a separately validated immutable snapshot.
[Serializable]
public sealed class NearbyPlayerNotificationRule
{
    public Guid RuleId { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = "Player notification";
    public NearbyPlayerNameMode NameMode { get; set; } = NearbyPlayerNameMode.Exact;
    public string Pattern { get; set; } = string.Empty;
    public List<uint> HomeWorldIds { get; set; } = new();
    public List<uint> TerritoryIds { get; set; } = new();
    public List<uint> OnlineStatusIds { get; set; } = new();
    public NearbyPlayerTriggerMode TriggerMode { get; set; } = NearbyPlayerTriggerMode.Appearance;
    public int CooldownSeconds { get; set; } = 300;
    public bool LocalChat { get; set; } = true;
    public bool Toast { get; set; }
    public bool Notification { get; set; } = true;
    public bool Speech { get; set; }
    // Nullable distinguishes old imports with text from explicitly opted-in actions.
    public bool? CommandsEnabled { get; set; }
    public NearbyPlayerEntryMode EntryMode { get; set; } = NearbyPlayerEntryMode.SlashCommand;
    public List<string> Commands { get; set; } = new();
    public bool LegacyGroup { get; set; }
    public string MigrationNote { get; set; } = string.Empty;
}
