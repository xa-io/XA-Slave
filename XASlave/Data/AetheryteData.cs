using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XASlave.Data;

/// <summary>
/// Aetheryte data structure linking aetheryte names to their ZoneIDs
/// Provides static methods for aetheryte-related data lookup
/// </summary>
public static class AetheryteData
{
    /// <summary>
    /// Cached list of aetheryte entries with their associated ZoneIDs
    /// </summary>
    private static List<AetheryteEntry>? _cachedAetherytes;

    /// <summary>
    /// Represents an aetheryte with its name and associated ZoneID
    /// </summary>
    public record AetheryteEntry(string Name, uint AetheryteId, uint ZoneId, string ZoneName);

    /// <summary>
    /// Gets all aetherytes with their names and ZoneIDs from Lumina sheets
    /// </summary>
    /// <returns>List of aetheryte entries</returns>
    public static List<AetheryteEntry> GetAetherytesWithZoneIds()
    {
        if (_cachedAetherytes != null)
            return _cachedAetherytes;

        var aetherytes = new List<AetheryteEntry>();
        
        try
        {
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            var territorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            
            if (aetheryteSheet != null && territorySheet != null)
            {
                foreach (var aetheryte in aetheryteSheet)
                {
                    if (!aetheryte.IsAetheryte)
                        continue;

                    var aetheryteName = aetheryte.PlaceName.ValueNullable?.Name.ToString();
                    if (string.IsNullOrWhiteSpace(aetheryteName))
                        continue;

                    // Get the territory/zone reference - try Territory first, then fallback methods
                    uint zoneId = 0;
                    bool territoryFound = false;
                    
                    try 
                    { 
                        zoneId = aetheryte.Territory.RowId;
                        territoryFound = true;
                    }
                    catch 
                    {
                        // Territory property not accessible, try alternative methods
                    }

                    // If we couldn't get the territory, skip this aetheryte for now
                    if (!territoryFound || zoneId == 0)
                        continue;
                    var territoryRow = territorySheet.GetRowOrDefault(zoneId);
                    var zoneName = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown Zone";

                    aetherytes.Add(new AetheryteEntry(aetheryteName, aetheryte.RowId, zoneId, zoneName));
                }
            }
        }
        catch
        {
            // Return empty list if sheet lookup fails
        }

        _cachedAetherytes = aetherytes.OrderBy(x => x.Name).ToList();
        return _cachedAetherytes;
    }

    /// <summary>
    /// Gets aetheryte names grouped by ZoneID
    /// </summary>
    /// <returns>Dictionary mapping ZoneID to list of aetheryte names in that zone</returns>
    public static Dictionary<uint, List<string>> GetAetherytesByZoneId()
    {
        var aetherytes = GetAetherytesWithZoneIds();
        return aetherytes
            .GroupBy(x => x.ZoneId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Name).OrderBy(name => name).ToList()
            );
    }

    /// <summary>
    /// Gets the ZoneID for a specific aetheryte name
    /// </summary>
    /// <param name="aetheryteName">Name of the aetheryte</param>
    /// <returns>ZoneID if found, 0 if not found</returns>
    public static uint GetZoneIdForAetheryte(string aetheryteName)
    {
        var aetherytes = GetAetherytesWithZoneIds();
        var entry = aetherytes.FirstOrDefault(x => 
            string.Equals(x.Name, aetheryteName, System.StringComparison.OrdinalIgnoreCase));
        return entry?.ZoneId ?? 0;
    }

    /// <summary>
    /// Gets aetheryte names in a specific zone
    /// </summary>
    /// <param name="zoneId">ZoneID to filter by</param>
    /// <returns>List of aetheryte names in the specified zone</returns>
    public static List<string> GetAetherytesInZone(uint zoneId)
    {
        var aetherytes = GetAetherytesWithZoneIds();
        return aetherytes
            .Where(x => x.ZoneId == zoneId)
            .Select(x => x.Name)
            .OrderBy(name => name)
            .ToList();
    }

    /// <summary>
    /// Gets the zone name for a specific aetheryte
    /// </summary>
    /// <param name="aetheryteName">Name of the aetheryte</param>
    /// <returns>Zone name if found, "Unknown" if not found</returns>
    public static string GetZoneNameForAetheryte(string aetheryteName)
    {
        var aetherytes = GetAetherytesWithZoneIds();
        var entry = aetherytes.FirstOrDefault(x => 
            string.Equals(x.Name, aetheryteName, System.StringComparison.OrdinalIgnoreCase));
        return entry?.ZoneName ?? "Unknown";
    }

    /// <summary>
    /// Clears the cached aetheryte data (useful for testing or data refresh)
    /// </summary>
    public static void ClearCache()
    {
        _cachedAetherytes = null;
    }

    /// <summary>
    /// Checks if the current character is in the correct zone for the specified aetheryte
    /// </summary>
    /// <param name="aetheryteName">Name of the aetheryte to check against</param>
    /// <returns>True if in the correct zone, false otherwise</returns>
    public static bool IsInCorrectZoneForAetheryte(string aetheryteName)
    {
        if (string.IsNullOrWhiteSpace(aetheryteName))
            return false;

        var expectedZoneId = GetZoneIdForAetheryteWithFallback(aetheryteName);
        if (expectedZoneId == 0)
            return false; // Aetheryte not found

        var currentZoneId = Plugin.ClientState.TerritoryType;
        return currentZoneId == expectedZoneId;
    }

    /// <summary>
    /// Fallback aetheryte to ZoneID mappings for use when Lumina lookup fails
    /// Generated: 2026-03-21 15:57:17
    /// Total aetherytes: 107
    /// </summary>
    public static readonly Dictionary<string, uint> AetheryteToZoneIdFallback = new()
    {
        // La Noscea
        {"Limsa Lominsa Lower Decks", 129}, // Zone [129] Limsa Lominsa Lower Decks
        {"Summerford Farms", 134}, // Zone [134] Middle La Noscea
        {"Moraby Drydocks", 135}, // Zone [135] Lower La Noscea
        {"Costa del Sol", 137}, // Zone [137] Eastern La Noscea
        {"Wineport", 137}, // Zone [137] Eastern La Noscea
        {"Swiftperch", 138}, // Zone [138] Western La Noscea
        {"Aleport", 138}, // Zone [138] Western La Noscea
        {"Camp Bronze Lake", 139}, // Zone [139] Upper La Noscea
        {"Camp Overlook", 180}, // Zone [180] Outer La Noscea
        {"Wolves' Den Pier", 250}, // Zone [250] Wolves' Den Pier

        // The Black Shroud
        {"New Gridania", 132}, // Zone [132] New Gridania
        {"Bentbranch Meadows", 148}, // Zone [148] Central Shroud
        {"The Hawthorne Hut", 152}, // Zone [152] East Shroud
        {"Quarrymill", 153}, // Zone [153] South Shroud
        {"Camp Tranquil", 153}, // Zone [153] South Shroud
        {"Fallgourd Float", 154}, // Zone [154] North Shroud

        // Thanalan
        {"Ul'dah - Steps of Nald", 130}, // Zone [130] Ul'dah - Steps of Nald
        {"Horizon", 140}, // Zone [140] Western Thanalan
        {"Black Brush Station", 141}, // Zone [141] Central Thanalan
        {"Camp Drybone", 145}, // Zone [145] Eastern Thanalan
        {"Little Ala Mhigo", 146}, // Zone [146] Southern Thanalan
        {"Forgotten Springs", 146}, // Zone [146] Southern Thanalan
        {"Camp Bluefog", 147}, // Zone [147] Northern Thanalan
        {"Ceruleum Processing Plant", 147}, // Zone [147] Northern Thanalan
        {"The Gold Saucer", 144}, // Zone [144] The Gold Saucer
        {"Revenant's Toll", 156}, // Zone [156] Mor Dhona

        // Ishgard
        {"Foundation", 418}, // Zone [418] Foundation
        {"Camp Dragonhead", 155}, // Zone [155] Coerthas Central Highlands
        {"Falcon's Nest", 397}, // Zone [397] Coerthas Western Highlands
        {"Camp Cloudtop", 401}, // Zone [401] The Sea of Clouds
        {"Ok' Zundu", 401}, // Zone [401] The Sea of Clouds
        {"Helix", 402}, // Zone [402] Azys Lla
        {"Idyllshire", 478}, // Zone [478] Idyllshire
        {"Tailfeather", 398}, // Zone [398] The Dravanian Forelands
        {"Anyx Trine", 398}, // Zone [398] The Dravanian Forelands
        {"Moghome", 400}, // Zone [400] The Churning Mists
        {"Zenith", 400}, // Zone [400] The Churning Mists

        // Gyr Abania
        {"Rhalgr's Reach", 635}, // Zone [635] Rhalgr's Reach
        {"Castrum Oriens", 612}, // Zone [612] The Fringes
        {"The Peering Stones", 612}, // Zone [612] The Fringes
        {"Ala Gannha", 620}, // Zone [620] The Peaks
        {"Ala Ghiri", 620}, // Zone [620] The Peaks
        {"Porta Praetoria", 621}, // Zone [621] The Lochs
        {"The Ala Mhigan Quarter", 621}, // Zone [621] The Lochs

        // The Far East
        {"Kugane", 628}, // Zone [628] Kugane
        {"Tamamizu", 613}, // Zone [613] The Ruby Sea
        {"Onokoro", 613}, // Zone [613] The Ruby Sea
        {"Namai", 614}, // Zone [614] Yanxia
        {"The House of the Fierce", 614}, // Zone [614] Yanxia
        {"Reunion", 622}, // Zone [622] The Azim Steppe
        {"The Dawn Throne", 622}, // Zone [622] The Azim Steppe
        {"Dhoro Iloh", 622}, // Zone [622] The Azim Steppe
        {"The Doman Enclave", 759}, // Zone [759] The Doman Enclave

        // Norvrandt
        {"The Crystarium", 819}, // Zone [819] The Crystarium
        {"Eulmore", 820}, // Zone [820] Eulmore
        {"Fort Jobb", 813}, // Zone [813] Lakeland
        {"The Ostall Imperative", 813}, // Zone [813] Lakeland
        {"Stilltide", 814}, // Zone [814] Kholusia
        {"Wright", 814}, // Zone [814] Kholusia
        {"Tomra", 814}, // Zone [814] Kholusia
        {"Mord Souq", 815}, // Zone [815] Amh Araeng
        {"The Inn at Journey's Head", 815}, // Zone [815] Amh Araeng
        {"Twine", 815}, // Zone [815] Amh Araeng
        {"Lydha Lran", 816}, // Zone [816] Il Mheg
        {"Pla Enni", 816}, // Zone [816] Il Mheg
        {"Wolekdorf", 816}, // Zone [816] Il Mheg
        {"Slitherbough", 817}, // Zone [817] The Rak'tika Greatwood
        {"Fanow", 817}, // Zone [817] The Rak'tika Greatwood
        {"The Ondo Cups", 818}, // Zone [818] The Tempest
        {"The Macarenses Angle", 818}, // Zone [818] The Tempest

        // The Northern Empty & Ilsabard
        {"Old Sharlayan", 962}, // Zone [962] Old Sharlayan
        {"Radz-at-Han", 963}, // Zone [963] Radz-at-Han
        {"The Archeion", 956}, // Zone [956] Labyrinthos
        {"Sharlayan Hamlet", 956}, // Zone [956] Labyrinthos
        {"Aporia", 956}, // Zone [956] Labyrinthos
        {"Yedlihmad", 957}, // Zone [957] Thavnair
        {"The Great Work", 957}, // Zone [957] Thavnair
        {"Palaka's Stand", 957}, // Zone [957] Thavnair
        {"Camp Broken Glass", 958}, // Zone [958] Garlemald
        {"Tertium", 958}, // Zone [958] Garlemald

        // Elpis
        {"Anagnorisis", 961}, // Zone [961] Elpis
        {"The Twelve Wonders", 961}, // Zone [961] Elpis
        {"Poieten Oikos", 961}, // Zone [961] Elpis

        // The Sea of Stars
        {"Sinus Lacrimarum", 959}, // Zone [959] Mare Lamentorum
        {"Bestways Burrow", 959}, // Zone [959] Mare Lamentorum
        {"Reah Tahra", 960}, // Zone [960] Ultima Thule
        {"Abode of the Ea", 960}, // Zone [960] Ultima Thule
        {"Base Omicron", 960}, // Zone [960] Ultima Thule

        // Yok Tural
        {"Tuliyollal", 1185}, // Zone [1185] Tuliyollal
        {"Wachunpelo", 1187}, // Zone [1187] Urqopacha
        {"Worlar's Echo", 1187}, // Zone [1187] Urqopacha
        {"Ok'hanu", 1188}, // Zone [1188] Kozama'uka
        {"Many Fires", 1188}, // Zone [1188] Kozama'uka
        {"Earthenshire", 1188}, // Zone [1188] Kozama'uka
        {"Dock Poga", 1188}, // Zone [1188] Kozama'uka
        {"Iq Br'aax", 1189}, // Zone [1189] Yak T'el
        {"Mamook", 1189}, // Zone [1189] Yak T'el

        // Xak Tural
        {"Solution Nine", 1186}, // Zone [1186] Solution Nine
        {"Hhusatahwi", 1190}, // Zone [1190] Shaaloani
        {"Sheshenewezi Springs", 1190}, // Zone [1190] Shaaloani
        {"Mehwahhetsoan", 1190}, // Zone [1190] Shaaloani
        {"Leynode Mnemo", 1192}, // Zone [1192] Living Memory
        {"Leynode Aero", 1192}, // Zone [1192] Living Memory
        {"Leynode Pyro", 1192}, // Zone [1192] Living Memory
        {"Yyasulani Station", 1191}, // Zone [1191] Heritage Found
        {"The Outskirts", 1191}, // Zone [1191] Heritage Found
        {"Electrope Strike", 1191}, // Zone [1191] Heritage Found
    };

    /// <summary>
    /// Gets ZoneID for aetheryte with fallback to static dictionary
    /// </summary>
    /// <param name="aetheryteName">Name of the aetheryte</param>
    /// <returns>ZoneID if found, 0 if not found</returns>
    public static uint GetZoneIdForAetheryteWithFallback(string aetheryteName)
    {
        // Try Lumina lookup first
        var aetherytes = GetAetherytesWithZoneIds();
        var entry = aetherytes.FirstOrDefault(x => 
            string.Equals(x.Name, aetheryteName, StringComparison.OrdinalIgnoreCase));
        
        if (entry != null)
            return entry.ZoneId;
        
        // Fallback to static dictionary
        if (AetheryteToZoneIdFallback.TryGetValue(aetheryteName, out var fallbackZoneId))
            return fallbackZoneId;
        
        return 0; // Not found
    }
}
