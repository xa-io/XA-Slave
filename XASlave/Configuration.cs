using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace XASlave;

/// <summary>
/// Persistent per-character data for Monthly Relogger table columns.
/// Keyed by "Name@World" in Configuration.ReloggerCharacterInfo.
/// Populated from AutoRetainer config, XA Database, or relogger runs.
/// </summary>
[Serializable]
public class ReloggerCharacterData
{
    public long CID { get; set; }
    public int HighestLevel { get; set; }
    public int Gil { get; set; }
    public string FcName { get; set; } = "";
    public long FCID { get; set; }
    public DateTime LastLoggedIn { get; set; } = DateTime.MinValue;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Auto-collection on login — opens saddlebag/FC windows to collect data
    public bool AutoCollectOnLogin { get; set; } = false;
    public bool AutoCollectSaddlebag { get; set; } = true;
    public bool AutoCollectFc { get; set; } = true;

    // Delay before auto-collection starts after login (seconds)
    public float AutoCollectDelaySeconds { get; set; } = 8f;

    // IPC panel — live value polling
    public bool IpcLivePullsEnabled { get; set; } = false;
    public int IpcLivePullIntervalSeconds { get; set; } = 10;

    // ── Monthly Relogger ──
    // Character list in "Name@World" format — persisted across sessions
    public List<string> ReloggerCharacters { get; set; } = new();

    // Per-character action toggles
    public bool ReloggerDoTextAdvance { get; set; } = true;
    public bool ReloggerDoRemoveSprout { get; set; } = true;
    public bool ReloggerDoOpenInventory { get; set; } = true;
    public bool ReloggerDoOpenArmouryChest { get; set; } = true;
    public bool ReloggerDoOpenSaddlebags { get; set; } = true;
    public bool ReloggerDoReturnToHome { get; set; } = true;
    public bool ReloggerDoReturnToFc { get; set; } = true;
    public bool ReloggerDoParseForXaDatabase { get; set; } = true;

    // Region filter for character list display
    public string ReloggerRegionFilter { get; set; } = "All";

    // Per-character persistent data for table columns (Lv, Gil, FC, In FC, Last Logged In).
    // Keyed by "Name@World". Updated from AutoRetainer imports and relogger runs.
    public Dictionary<string, ReloggerCharacterData> ReloggerCharacterInfo { get; set; } = new();

    // Legacy CID → last login timestamp. Migrated to ReloggerCharacterInfo on load.
    public Dictionary<long, DateTime> ReloggerLastSeen { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
