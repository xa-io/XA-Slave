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
    public string PersonalEstate { get; set; } = "";
    public string Apartment { get; set; } = "";
    public string FcEstate { get; set; } = "";
    public string CurrentWorld { get; set; } = "";
    public int RetainerCount { get; set; }
    public int SubmarineCount { get; set; }
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
    public bool ReloggerDoEnableArMultiOnComplete { get; set; } = false;

    // Region filter for character list display
    public string ReloggerRegionFilter { get; set; } = "All";

    // Per-character persistent data for table columns (Lv, Gil, FC, In FC, Last Logged In).
    // Keyed by "Name@World". Updated from AutoRetainer imports and relogger runs.
    public Dictionary<string, ReloggerCharacterData> ReloggerCharacterInfo { get; set; } = new();

    // Legacy CID → last login timestamp. Migrated to ReloggerCharacterInfo on load.
    public Dictionary<long, DateTime> ReloggerLastSeen { get; set; } = new();

    // ── Refresh AR Subs/Bell ──
    public List<string> RefreshSubsCharacters { get; set; } = new();

    // ── FC Permissions Updater ──
    public List<string> FcPermsCharacters { get; set; } = new();

    // ── AR Pre-Processing ──
    // Master toggle — when enabled, runs collection steps on login BEFORE AR starts retainer processing
    // Uses AR Suppressed pattern: suppress AR → run steps → un-suppress AR
    public bool ArPreProcessEnabled { get; set; } = false;
    public float ArPreProcessLoginDelay { get; set; } = 5f;
    // Per-step toggles — what to do before AR processes retainers
    public bool ArPreProcessOpenInventory { get; set; } = true;
    public bool ArPreProcessOpenArmouryChest { get; set; } = true;
    public bool ArPreProcessOpenSaddlebags { get; set; } = true;
    public bool ArPreProcessFcWindow { get; set; } = true;
    public bool ArPreProcessSaveToXaDatabase { get; set; } = true;

    // ── AR Post-Processing ──
    // Master toggle — when enabled, registers with AR for character post-processing in multi-mode
    public bool ArPostProcessEnabled { get; set; } = false;
    // Per-step toggles — what to do after AR finishes each character
    public bool ArPostProcessOpenInventory { get; set; } = true;
    public bool ArPostProcessOpenArmouryChest { get; set; } = true;
    public bool ArPostProcessOpenSaddlebags { get; set; } = true;
    public bool ArPostProcessFcWindow { get; set; } = true;
    public bool ArPostProcessSaveToXaDatabase { get; set; } = true;

    // ── Window Renamer ──
    public bool WindowRenamerEnabled { get; set; } = false;
    public string WindowRenamerTitle { get; set; } = "";
    public bool WindowRenamerUseProcessId { get; set; } = false;

    // ── City Chat Flooder ──
    public List<string> FloorderSelectedWorlds { get; set; } = new();
    public List<string> FloorderSelectedCities { get; set; } = new();
    public List<string> FloorderCustomCities { get; set; } = new();
    public List<string> FloorderAnnouncements { get; set; } = new();
    public string FloorderChatChannel { get; set; } = "/echo";
    public float FloorderWaitBetweenCities { get; set; } = 3.0f;
    public float FloorderWaitAfterAnnounce { get; set; } = 1.0f;
    public bool FloorderEnableLooping { get; set; } = false;
    public float FloorderLoopDelayMinutes { get; set; } = 5.0f;
    public bool FloorderInitialized { get; set; } = false;

    public void InitializeFloorderDefaults()
    {
        if (FloorderInitialized) return;
        FloorderInitialized = true;
        if (FloorderSelectedCities.Count == 0)
        {
            FloorderSelectedCities.AddRange(new[] { "Limsa Lominsa Lower Decks", "New Gridania", "Ul'dah - Steps of Nald" });
        }
        if (FloorderAnnouncements.Count == 0)
        {
            FloorderAnnouncements.AddRange(new[]
            {
                "I'm the real warrior of light",
                "What are you talking about, I'm totally real.",
                "I'm just looking for a new sidequest.",
            });
        }
        Save();
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
