using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>
/// Reads AutoRetainer's DefaultConfig.json to extract character data.
/// Path: {pluginConfigs}/AutoRetainer/DefaultConfig.json
///
/// Uses the same approach as AR Parser (Python) but implemented in C#.
/// The OfflineData array contains character entries with Name, World, Gil,
/// RetainerData, ClassJobLevelArray, FCID, etc.
/// </summary>
public sealed class AutoRetainerConfigReader
{
    private readonly IPluginLog log;
    private readonly string pluginConfigsBasePath;

    /// <summary>OfflineData entries matched by exclusions during the last successful honored read.</summary>
    public int LastExcludedCharacterCount { get; private set; }
    internal AutoRetainerExclusionSnapshot? LastExclusionSnapshot { get; private set; }

    public string ExclusionStatusSuffix => LastExcludedCharacterCount > 0
        ? $" ({LastExcludedCharacterCount} excluded by AutoRetainer)"
        : string.Empty;

    /// <summary>Character data extracted from AutoRetainer config.</summary>
    public record ArCharacterInfo(
        string Name,
        string World,
        string CurrentWorld,
        long CID,
        int Gil,
        int HighestLevel,
        int RetainerCount,
        int SubmarineCount,
        long TotalRetainerGil,
        int Ventures,
        int InventorySpace,
        bool Enabled,
        string FcName,
        long FcPoints,
        long FCID
    );

    public AutoRetainerConfigReader(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        // GetPluginConfigDirectory() returns e.g. "pluginConfigs/XASlave"
        // Go up one level to get the base "pluginConfigs" directory
        var ownConfigDir = pluginInterface.GetPluginConfigDirectory();
        pluginConfigsBasePath = Path.GetDirectoryName(ownConfigDir) ?? ownConfigDir;

    }

    /// <summary>Path to AutoRetainer's DefaultConfig.json.</summary>
    public string GetAutoRetainerConfigPath()
    {
        return Path.Combine(pluginConfigsBasePath, "AutoRetainer", "DefaultConfig.json");
    }

    /// <summary>Returns true if the AutoRetainer config file exists.</summary>
    public bool ConfigFileExists()
    {
        return File.Exists(GetAutoRetainerConfigPath());
    }

    internal AutoRetainerExclusionSnapshot ReadExclusionSnapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GetAutoRetainerConfigPath()));
        return AutoRetainerExclusionPolicy.ReadSnapshot(document.RootElement);
    }

    /// <summary>
    /// Reads AutoRetainer's DefaultConfig.json. Exclusion checks can be disabled, or matched rows retained for reversible list filtering.
    /// Returns a list of ArCharacterInfo records sorted by Region/DC/World.
    /// </summary>
    /// <exception cref="InvalidDataException">Exclusions cannot be safely checked; callers must preserve existing data.</exception>
    public List<ArCharacterInfo> ReadCharacters(bool honorExclusions = true, bool retainExcludedCharacters = false)
    {
        LastExcludedCharacterCount = 0;
        LastExclusionSnapshot = null;
        var configPath = GetAutoRetainerConfigPath();
        if (!File.Exists(configPath))
        {
            log.Warning($"[XASlave] AR config not found: {configPath}");
            return new List<ArCharacterInfo>();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var exclusionSnapshot = honorExclusions ? AutoRetainerExclusionPolicy.ReadSnapshot(root) : null;
            var excludedContentIds = exclusionSnapshot?.ContentIds ?? new HashSet<ulong>();
            var excludedCount = 0;

            // Extract FC data first (recursive search for HolderChara objects)
            var fcData = ExtractFcData(root);

            // Extract characters from OfflineData array
            var characters = new List<ArCharacterInfo>();

            if (root.TryGetProperty("OfflineData", out var offlineData) && offlineData.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in offlineData.EnumerateArray())
                {
                    // Compare the full unsigned identity before parsing the retained character data.
                    // Keep this outside its per-entry catch: an unknown identity must cancel the whole import.
                    if (AutoRetainerExclusionPolicy.ShouldExcludeCharacter(entry, excludedContentIds))
                    {
                        excludedCount++;
                        if (!retainExcludedCharacters)
                            continue;
                    }
                    try
                    {
                        var charInfo = ParseCharacterEntry(entry, fcData);
                        if (charInfo != null)
                            characters.Add(charInfo);
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"[XASlave] Failed to parse AR character entry: {ex.Message}");
                    }
                }
            }

            // Sort by Region → DC → World → Name
            characters = characters
                .OrderBy(c => WorldData.GetSortKey(c.World))
                .ThenBy(c => c.Name)
                .ToList();

            LastExcludedCharacterCount = excludedCount;
            LastExclusionSnapshot = exclusionSnapshot;
            if (excludedCount > 0)
                log.Information($"[XASlave] AR config: {excludedCount} globally excluded character entries ({(retainExcludedCharacters ? "retained for optional list filtering" : "skipped")}).");
            return characters;
        }
        catch (InvalidDataException ex)
        {
            // Do not turn an unreadable blacklist into an empty successful import/refresh.
            log.Warning($"[XASlave] AR import cancelled: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] Failed to read AR config: {ex.Message}");
            return new List<ArCharacterInfo>();
        }
    }

    /// <summary>Parse a single character entry from OfflineData.</summary>
    private ArCharacterInfo? ParseCharacterEntry(JsonElement entry, Dictionary<long, (string Name, long Points)> fcData)
    {
        if (!entry.TryGetProperty("CID", out var cidProp)) return null;
        if (!entry.TryGetProperty("Name", out var nameProp)) return null;
        if (!entry.TryGetProperty("World", out var worldProp)) return null;

        ulong unsignedCid = 0;
        if (cidProp.ValueKind == JsonValueKind.Number)
            cidProp.TryGetUInt64(out unsignedCid);
        else if (cidProp.ValueKind == JsonValueKind.String)
            ulong.TryParse(cidProp.GetString(), out unsignedCid);
        // Preserve the complete identity in the existing signed configuration field.
        var cid = unchecked((long)unsignedCid);
        var name = nameProp.GetString() ?? "Unknown";
        var world = worldProp.GetString() ?? "Unknown";

        var currentWorld = world; // default to homeworld
        if (entry.TryGetProperty("CurrentWorld", out var cwProp) && cwProp.ValueKind == JsonValueKind.String)
        {
            var cw = cwProp.GetString();
            if (!string.IsNullOrEmpty(cw)) currentWorld = cw;
        }

        if (string.IsNullOrWhiteSpace(name) || name == "Unknown") return null;

        var gil = 0;
        if (entry.TryGetProperty("Gil", out var gilProp) && gilProp.ValueKind == JsonValueKind.Number)
            gilProp.TryGetInt32(out gil);

        var ventures = 0;
        if (entry.TryGetProperty("Ventures", out var venturesProp) && venturesProp.ValueKind == JsonValueKind.Number)
            venturesProp.TryGetInt32(out ventures);

        var inventorySpace = 0;
        if (entry.TryGetProperty("InventorySpace", out var invProp) && invProp.ValueKind == JsonValueKind.Number)
            invProp.TryGetInt32(out inventorySpace);

        var enabled = false;
        if (entry.TryGetProperty("Enabled", out var enabledProp))
        {
            if (enabledProp.ValueKind == JsonValueKind.True) enabled = true;
            else if (enabledProp.ValueKind == JsonValueKind.False) enabled = false;
        }

        // Highest class/job level from ClassJobLevelArray
        var highestLevel = 0;
        if (entry.TryGetProperty("ClassJobLevelArray", out var levelArr) && levelArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var lv in levelArr.EnumerateArray())
            {
                if (lv.ValueKind == JsonValueKind.Number && lv.TryGetInt32(out var val))
                {
                    if (val > highestLevel) highestLevel = val;
                }
            }
        }

        // Retainer data
        var retainerCount = 0;
        var totalRetainerGil = 0L;
        if (entry.TryGetProperty("RetainerData", out var retArr) && retArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ret in retArr.EnumerateArray())
            {
                retainerCount++;
                if (ret.TryGetProperty("Gil", out var retGilProp) && retGilProp.ValueKind == JsonValueKind.Number && retGilProp.TryGetInt64(out var retGil))
                    totalRetainerGil += retGil;
            }
        }

        // FC data - match by CID
        var fcName = string.Empty;
        long fcPoints = 0;
        if (fcData.TryGetValue(cid, out var fc))
        {
            fcName = fc.Name;
            fcPoints = fc.Points;
        }

        long fcid = 0;
        if (entry.TryGetProperty("FCID", out var fcidProp) && fcidProp.ValueKind == JsonValueKind.Number)
            fcidProp.TryGetInt64(out fcid);

        // Submarine data - count entries in OfflineSubmarineData or AdditionalSubmarineData
        var submarineCount = 0;
        if (entry.TryGetProperty("OfflineSubmarineData", out var subArr) && subArr.ValueKind == JsonValueKind.Array)
        {
            submarineCount = subArr.GetArrayLength();
        }
        else if (entry.TryGetProperty("SubmarineData", out var subArr2) && subArr2.ValueKind == JsonValueKind.Array)
        {
            submarineCount = subArr2.GetArrayLength();
        }
        else if (entry.TryGetProperty("Submarines", out var subArr3) && subArr3.ValueKind == JsonValueKind.Array)
        {
            submarineCount = subArr3.GetArrayLength();
        }

        return new ArCharacterInfo(
            Name: name,
            World: world,
            CurrentWorld: currentWorld,
            CID: cid,
            Gil: gil,
            HighestLevel: highestLevel,
            RetainerCount: retainerCount,
            SubmarineCount: submarineCount,
            TotalRetainerGil: totalRetainerGil,
            Ventures: ventures,
            InventorySpace: inventorySpace,
            Enabled: enabled,
            FcName: fcName,
            FcPoints: fcPoints,
            FCID: fcid
        );
    }

    /// <summary>
    /// Recursively searches the JSON for objects containing "HolderChara"
    /// to extract FC name and FC points per character CID.
    /// Same approach as AR Parser's extract_fc_data().
    /// </summary>
    private Dictionary<long, (string Name, long Points)> ExtractFcData(JsonElement root)
    {
        var fcData = new Dictionary<long, (string Name, long Points)>();
        RecursiveFcSearch(root, fcData);
        return fcData;
    }

    private void RecursiveFcSearch(JsonElement element, Dictionary<long, (string Name, long Points)> fcData)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("HolderChara", out var holderProp))
                {
                    var holderId = holderProp.GetInt64();
                    var fcName = "Unknown FC";
                    long fcPoints = 0;

                    if (element.TryGetProperty("Name", out var fcNameProp))
                        fcName = fcNameProp.GetString() ?? "Unknown FC";
                    if (element.TryGetProperty("FCPoints", out var fcPointsProp))
                        fcPoints = fcPointsProp.GetInt64();

                    fcData[holderId] = (fcName, fcPoints);
                }

                foreach (var prop in element.EnumerateObject())
                    RecursiveFcSearch(prop.Value, fcData);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    RecursiveFcSearch(item, fcData);
                break;
        }
    }
}
