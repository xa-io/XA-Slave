using System.Collections.Generic;
using System.Linq;

namespace XASlave.Data;

/// <summary>
/// Static FFXIV world/data center/region mappings.
/// Sourced from dfunc.lua homeworld_lookup and game data.
/// Used for region-based sorting and filtering of character lists.
/// </summary>
public static class WorldData
{
    public record WorldInfo(uint Id, string Name, string DataCenter, string Region);

    /// <summary>Region display order for sorting: NA → EU → JP → OCE</summary>
    public static readonly string[] RegionOrder = { "NA", "EU", "JP", "OCE" };

    /// <summary>Data center display order within each region.</summary>
    public static readonly Dictionary<string, string[]> DataCenterOrder = new()
    {
        ["NA"] = new[] { "Aether", "Crystal", "Dynamis", "Primal" },
        ["EU"] = new[] { "Chaos", "Light" },
        ["JP"] = new[] { "Elemental", "Gaia", "Mana", "Meteor" },
        ["OCE"] = new[] { "Materia" },
    };

    /// <summary>All known game worlds with ID, name, DC, and region.</summary>
    public static readonly WorldInfo[] Worlds =
    {
        // ── NA — Aether ──
        new(73,  "Adamantoise", "Aether", "NA"),
        new(79,  "Cactuar",     "Aether", "NA"),
        new(54,  "Faerie",      "Aether", "NA"),
        new(63,  "Gilgamesh",   "Aether", "NA"),
        new(40,  "Jenova",      "Aether", "NA"),
        new(65,  "Midgardsormr","Aether", "NA"),
        new(99,  "Sargatanas",  "Aether", "NA"),
        new(57,  "Siren",       "Aether", "NA"),

        // ── NA — Crystal ──
        new(91,  "Balmung",     "Crystal", "NA"),
        new(34,  "Brynhildr",   "Crystal", "NA"),
        new(74,  "Coeurl",      "Crystal", "NA"),
        new(62,  "Diabolos",    "Crystal", "NA"),
        new(81,  "Goblin",      "Crystal", "NA"),
        new(75,  "Malboro",     "Crystal", "NA"),
        new(37,  "Mateus",      "Crystal", "NA"),
        new(41,  "Zalera",      "Crystal", "NA"),

        // ── NA — Dynamis ──
        new(408, "Cuchulainn",  "Dynamis", "NA"),
        new(411, "Golem",       "Dynamis", "NA"),
        new(406, "Halicarnassus","Dynamis", "NA"),
        new(409, "Kraken",      "Dynamis", "NA"),
        new(407, "Maduin",      "Dynamis", "NA"),
        new(404, "Marilith",    "Dynamis", "NA"),
        new(410, "Rafflesia",   "Dynamis", "NA"),
        new(405, "Seraph",      "Dynamis", "NA"),

        // ── NA — Primal ──
        new(78,  "Behemoth",    "Primal", "NA"),
        new(93,  "Excalibur",   "Primal", "NA"),
        new(53,  "Exodus",      "Primal", "NA"),
        new(35,  "Famfrit",     "Primal", "NA"),
        new(95,  "Hyperion",    "Primal", "NA"),
        new(55,  "Lamia",       "Primal", "NA"),
        new(64,  "Leviathan",   "Primal", "NA"),
        new(77,  "Ultros",      "Primal", "NA"),

        // ── EU — Chaos ──
        new(80,  "Cerberus",    "Chaos", "EU"),
        new(83,  "Louisoix",    "Chaos", "EU"),
        new(71,  "Moogle",      "Chaos", "EU"),
        new(39,  "Omega",       "Chaos", "EU"),
        new(401, "Phantom",     "Chaos", "EU"),
        new(97,  "Ragnarok",    "Chaos", "EU"),
        new(400, "Sagittarius", "Chaos", "EU"),
        new(85,  "Spriggan",    "Chaos", "EU"),

        // ── EU — Light ──
        new(402, "Alpha",       "Light", "EU"),
        new(36,  "Lich",        "Light", "EU"),
        new(66,  "Odin",        "Light", "EU"),
        new(56,  "Phoenix",     "Light", "EU"),
        new(403, "Raiden",      "Light", "EU"),
        new(67,  "Shiva",       "Light", "EU"),
        new(33,  "Twintania",   "Light", "EU"),
        new(42,  "Zodiark",     "Light", "EU"),

        // ── JP — Elemental ──
        new(90,  "Aegis",       "Elemental", "JP"),
        new(68,  "Atomos",      "Elemental", "JP"),
        new(45,  "Carbuncle",   "Elemental", "JP"),
        new(58,  "Garuda",      "Elemental", "JP"),
        new(94,  "Gungnir",     "Elemental", "JP"),
        new(49,  "Kujata",      "Elemental", "JP"),
        new(72,  "Tonberry",    "Elemental", "JP"),
        new(50,  "Typhon",      "Elemental", "JP"),

        // ── JP — Gaia ──
        new(43,  "Alexander",   "Gaia", "JP"),
        new(69,  "Bahamut",     "Gaia", "JP"),
        new(92,  "Durandal",    "Gaia", "JP"),
        new(46,  "Fenrir",      "Gaia", "JP"),
        new(59,  "Ifrit",       "Gaia", "JP"),
        new(98,  "Ridill",      "Gaia", "JP"),
        new(76,  "Tiamat",      "Gaia", "JP"),
        new(51,  "Ultima",      "Gaia", "JP"),

        // ── JP — Mana ──
        new(44,  "Anima",       "Mana", "JP"),
        new(23,  "Asura",       "Mana", "JP"),
        new(70,  "Chocobo",     "Mana", "JP"),
        new(47,  "Hades",       "Mana", "JP"),
        new(48,  "Ixion",       "Mana", "JP"),
        new(96,  "Masamune",    "Mana", "JP"),
        new(28,  "Pandaemonium","Mana", "JP"),
        new(61,  "Titan",       "Mana", "JP"),

        // ── JP — Meteor ──
        new(24,  "Belias",      "Meteor", "JP"),
        new(82,  "Mandragora",  "Meteor", "JP"),
        new(60,  "Ramuh",       "Meteor", "JP"),
        new(29,  "Shinryu",     "Meteor", "JP"),
        new(30,  "Unicorn",     "Meteor", "JP"),
        new(52,  "Valefor",     "Meteor", "JP"),
        new(31,  "Yojimbo",     "Meteor", "JP"),
        new(32,  "Zeromus",     "Meteor", "JP"),

        // ── OCE — Materia ──
        new(22,  "Bismarck",    "Materia", "OCE"),
        new(21,  "Ravana",      "Materia", "OCE"),
        new(86,  "Sephirot",    "Materia", "OCE"),
        new(87,  "Sophia",      "Materia", "OCE"),
        new(88,  "Zurvan",      "Materia", "OCE"),
    };

    // ── Lookup helpers ──

    private static readonly Dictionary<uint, WorldInfo> ById =
        Worlds.ToDictionary(w => w.Id);

    private static readonly Dictionary<string, WorldInfo> ByName =
        Worlds.ToDictionary(w => w.Name.ToLowerInvariant());

    public static WorldInfo? GetById(uint id) =>
        ById.TryGetValue(id, out var w) ? w : null;

    public static WorldInfo? GetByName(string name) =>
        ByName.TryGetValue(name.ToLowerInvariant(), out var w) ? w : null;

    /// <summary>
    /// Returns the sort key for a world, ordered by Region → DC → World name.
    /// Format: "0_00_WorldName" where first digit is region index, second is DC index.
    /// </summary>
    public static string GetSortKey(string worldName)
    {
        var w = GetByName(worldName);
        if (w == null) return "9_99_" + worldName;

        var regionIdx = System.Array.IndexOf(RegionOrder, w.Region);
        if (regionIdx < 0) regionIdx = 9;

        var dcIdx = 0;
        if (DataCenterOrder.TryGetValue(w.Region, out var dcs))
        {
            dcIdx = System.Array.IndexOf(dcs, w.DataCenter);
            if (dcIdx < 0) dcIdx = 9;
        }

        return $"{regionIdx}_{dcIdx:D2}_{w.Name}";
    }

    /// <summary>Returns "Region / DC" label for a world name.</summary>
    public static string GetRegionDcLabel(string worldName)
    {
        var w = GetByName(worldName);
        return w != null ? $"{w.Region} / {w.DataCenter}" : "Unknown";
    }
}
