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
    public bool OpenPluginOnLoad { get; set; } = false;
    public bool VerboseTaskLogging { get; set; } = false;

    // Auto-collection on login — opens saddlebag/FC windows to collect data
    public bool AutoCollectOnLogin { get; set; } = false;
    public bool AutoCollectArmouryChest { get; set; } = true;
    public bool AutoCollectSaddlebag { get; set; } = true;
    public bool AutoCollectJournal { get; set; } = true;
    public bool AutoCollectPersonalPlotInfo { get; set; } = true;
    public bool AutoCollectFc { get; set; } = true;
    public int AutoCollectCheckEveryHours { get; set; } = 0;
    public bool AutoCollectDisableWhenArMultiEnabled { get; set; } = false;

    // Delay before auto-collection starts after login (seconds)
    public float AutoCollectDelaySeconds { get; set; } = 8f;

    // IPC panel — live value polling
    public bool IpcLivePullsEnabled { get; set; } = false;
    public int IpcLivePullIntervalSeconds { get; set; } = 10;

    // ── Monthly Relogger ──
    // Character list in "Name@World" format — persisted across sessions
    public List<string> ReloggerCharacters { get; set; } = new();

    // ── Auto-Glam Weather ──
    public int AutoGlamWeatherClassJob { get; set; } = 1;
    public int AutoGlamWeatherSunnyPlate { get; set; } = 2;
    public int AutoGlamWeatherRainPlate { get; set; } = 3;
    public int AutoGlamWeatherFreezePlate { get; set; } = 1;
    public string AutoGlamWeatherClassJobOptions { get; set; } = "1";
    public string AutoGlamWeatherSunnyPlateOptions { get; set; } = "2";
    public string AutoGlamWeatherRainPlateOptions { get; set; } = "3";
    public string AutoGlamWeatherFreezePlateOptions { get; set; } = "1";
    public bool AutoGlamWeatherOptionsInitialized { get; set; } = false;
    public float AutoGlamWeatherCheckIntervalSeconds { get; set; } = 3.0f;

    // Per-character action toggles
    public bool ReloggerDoTextAdvance { get; set; } = true;
    public bool ReloggerDoRemoveSprout { get; set; } = true;
    public bool ReloggerDoOpenInventory { get; set; } = true;
    public bool ReloggerDoOpenArmouryChest { get; set; } = true;
    public bool ReloggerDoOpenSaddlebags { get; set; } = true;
    public bool ReloggerDoOpenJournal { get; set; } = true;
    public bool ReloggerDoReturnToHome { get; set; } = true;
    public bool ReloggerDoCollectPersonalPlotInfo { get; set; } = true;
    public bool ReloggerDoReturnToFc { get; set; } = true;
    public bool ReloggerDoParseForXaDatabase { get; set; } = true;
    public bool ReloggerDoLogoutOnComplete { get; set; } = false;
    public bool ReloggerDoEnableArMultiOnComplete { get; set; } = false;

    // Region filter for character list display
    public string ReloggerRegionFilter { get; set; } = "All";
    public int ReloggerStaleSelectDays { get; set; } = 20;

    // Per-character persistent data for table columns (Lv, Gil, FC, In FC, Last Logged In).
    // Keyed by "Name@World". Updated from AutoRetainer imports and relogger runs.
    public Dictionary<string, ReloggerCharacterData> ReloggerCharacterInfo { get; set; } = new();

    // Legacy CID → last login timestamp. Migrated to ReloggerCharacterInfo on load.
    public Dictionary<long, DateTime> ReloggerLastSeen { get; set; } = new();

    // ── Refresh AR Subs/Bell ──
    public List<string> RefreshSubsCharacters { get; set; } = new();

    // ── Prep Logistics ──
    public List<string> PrepLogisticsCharacters { get; set; } = new();
    public string PrepLogisticsTargetWorld { get; set; } = string.Empty;
    public string PrepLogisticsTargetAetheryte { get; set; } = string.Empty;
    public bool PrepLogisticsEnableArMultiOnComplete { get; set; } = true;
    public bool PrepLogisticsLogoutOnComplete { get; set; } = false;

    // ── FC Permissions Updater ──
    public List<string> FcPermsCharacters { get; set; } = new();

    // ── AR Pre-Processing ──
    // Master toggle — when enabled, runs collection steps on login BEFORE AR starts retainer processing
    // Uses AR Suppressed pattern: suppress AR → run steps → un-suppress AR
    public bool ArPreProcessEnabled { get; set; } = false;
    public float ArPreProcessLoginDelay { get; set; } = 5f;
    public int ArPrePostCheckEveryHours { get; set; } = 0;
    public bool ArShipExplorationBailoutEnabled { get; set; } = false;
    public int ArShipExplorationBailoutSeconds { get; set; } = 30;
    // Per-step toggles — what to do before AR processes retainers
    public bool ArPreProcessOpenInventory { get; set; } = true;
    public bool ArPreProcessOpenArmouryChest { get; set; } = true;
    public bool ArPreProcessOpenSaddlebags { get; set; } = true;
    public bool ArPreProcessOpenJournal { get; set; } = true;
    public bool ArPreProcessCollectPersonalPlotInfo { get; set; } = true;
    public bool ArPreProcessFcWindow { get; set; } = true;
    public bool ArPreProcessSaveToXaDatabase { get; set; } = true;

    // ── AR Post-Processing ──
    // Master toggle — when enabled, registers with AR for character post-processing in multi-mode
    public bool ArPostProcessEnabled { get; set; } = false;
    // Per-step toggles — what to do after AR finishes each character
    public bool ArPostProcessOpenInventory { get; set; } = true;
    public bool ArPostProcessOpenArmouryChest { get; set; } = true;
    public bool ArPostProcessOpenSaddlebags { get; set; } = true;
    public bool ArPostProcessOpenJournal { get; set; } = true;
    public bool ArPostProcessCollectPersonalPlotInfo { get; set; } = true;
    public bool ArPostProcessFcWindow { get; set; } = true;
    public bool ArPostProcessSaveToXaDatabase { get; set; } = true;
    public bool ArProcessLogEnabled { get; set; } = false;

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

    public bool InitializeAutoGlamWeatherDefaults()
    {
        if (AutoGlamWeatherOptionsInitialized)
            return false;

        var changed = false;
        AutoGlamWeatherOptionsInitialized = true;
        changed = true;

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherClassJobOptions)
            || (AutoGlamWeatherClassJobOptions == "1" && AutoGlamWeatherClassJob != 1))
        {
            AutoGlamWeatherClassJobOptions = AutoGlamWeatherClassJob > 0 ? AutoGlamWeatherClassJob.ToString() : "1";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherSunnyPlateOptions)
            || (AutoGlamWeatherSunnyPlateOptions == "2" && AutoGlamWeatherSunnyPlate != 2))
        {
            AutoGlamWeatherSunnyPlateOptions = AutoGlamWeatherSunnyPlate > 0 ? AutoGlamWeatherSunnyPlate.ToString() : "2";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherRainPlateOptions)
            || (AutoGlamWeatherRainPlateOptions == "3" && AutoGlamWeatherRainPlate != 3))
        {
            AutoGlamWeatherRainPlateOptions = AutoGlamWeatherRainPlate > 0 ? AutoGlamWeatherRainPlate.ToString() : "3";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherFreezePlateOptions)
            || (AutoGlamWeatherFreezePlateOptions == "1" && AutoGlamWeatherFreezePlate != 1))
        {
            AutoGlamWeatherFreezePlateOptions = AutoGlamWeatherFreezePlate > 0 ? AutoGlamWeatherFreezePlate.ToString() : "1";
            changed = true;
        }

        return changed;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
