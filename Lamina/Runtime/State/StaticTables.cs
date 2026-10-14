using Lamina.Models;

namespace Lamina.Runtime.State;

/// <summary> Dalamud-free lookup tables used by <see cref="GameCatalog"/> and the tracker. Pure data, unit-testable. </summary>
public static class StaticTables
{
    /// <summary> Weather display name → Lamina weather groups. A weather may belong to several groups. </summary>
    public static readonly IReadOnlyDictionary<string, WeatherGroup[]> WeatherGroupsByName = new Dictionary<string, WeatherGroup[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Clear Skies"]         = [WeatherGroup.Clear],
        ["Fair Skies"]          = [WeatherGroup.Clear],
        ["Clouds"]              = [WeatherGroup.Overcast],
        ["Fog"]                 = [WeatherGroup.Overcast],
        ["Overcast"]            = [WeatherGroup.Overcast],
        ["Rain"]                = [WeatherGroup.Wet],
        ["Showers"]             = [WeatherGroup.Wet],
        ["Thunder"]             = [WeatherGroup.Stormy],
        ["Thunderstorms"]       = [WeatherGroup.Stormy, WeatherGroup.Wet],
        ["Wind"]                = [WeatherGroup.Windy],
        ["Gales"]               = [WeatherGroup.Windy],
        ["Dust Storms"]         = [WeatherGroup.Windy],
        ["Sandstorms"]          = [WeatherGroup.Windy],
        ["Hot Spells"]          = [WeatherGroup.Hot],
        ["Heat Waves"]          = [WeatherGroup.Hot],
        ["Snow"]                = [WeatherGroup.Cold],
        ["Blizzards"]           = [WeatherGroup.Cold],
        ["Gloom"]               = [WeatherGroup.Arcane],
        ["Umbral Wind"]         = [WeatherGroup.Arcane, WeatherGroup.Windy],
        ["Umbral Static"]       = [WeatherGroup.Arcane],
        ["Umbral Flare"]        = [WeatherGroup.Arcane, WeatherGroup.Hot],
        ["Umbral Duststorms"]   = [WeatherGroup.Arcane, WeatherGroup.Windy],
        ["Umbral Levin"]        = [WeatherGroup.Arcane, WeatherGroup.Stormy],
        ["Umbral Tempest"]      = [WeatherGroup.Arcane, WeatherGroup.Stormy],
        ["Umbral Turbulence"]   = [WeatherGroup.Arcane, WeatherGroup.Windy],
        ["Moon Dust"]           = [WeatherGroup.Arcane],
        ["Astromagnetic Storm"] = [WeatherGroup.Arcane],
        ["Eruptions"]           = [WeatherGroup.Hot],
        ["Royal Levin"]         = [WeatherGroup.Stormy],
        ["Hyperelectricity"]    = [WeatherGroup.Stormy, WeatherGroup.Arcane],
        ["Everlasting Light"]   = [WeatherGroup.Clear, WeatherGroup.Arcane],
        ["Eternal Bliss"]       = [WeatherGroup.Clear, WeatherGroup.Arcane],
        ["Dragonstorms"]        = [WeatherGroup.Stormy, WeatherGroup.Arcane],
        ["Subterrain"]          = [WeatherGroup.Overcast],
        ["Shelf Clouds"]        = [WeatherGroup.Overcast],
    };

    /// <summary> Groups for a weather name, empty when unknown. </summary>
    public static IReadOnlySet<WeatherGroup> WeatherGroupsFor(string weatherName)
        => WeatherGroupsByName.TryGetValue(weatherName, out var groups) ? new HashSet<WeatherGroup>(groups) : new HashSet<WeatherGroup>();

    /// <summary> Physical ranged jobs (ClassJob row ids). ARC 5, BRD 23, MCH 31, DNC 38. </summary>
    public static readonly IReadOnlySet<uint> PhysicalRangedJobs = new HashSet<uint> { 5, 23, 31, 38 };

    /// <summary> Casters (ClassJob row ids). THM 7, ACN 26, BLM 25, SMN 27, RDM 35, BLU 36, PCT 42. </summary>
    public static readonly IReadOnlySet<uint> CasterJobs = new HashSet<uint> { 7, 25, 26, 27, 35, 36, 42 };

    /// <summary> Maps a ClassJob sheet row to a Lamina role. Role column: 1 Tank, 2 Melee DPS, 3 Ranged DPS, 4 Healer. </summary>
    public static JobRole? RoleFor(uint classJobId, byte sheetRole, sbyte dohDolJobIndex, uint classJobCategory)
    {
        if (dohDolJobIndex >= 0 || classJobId is >= 8 and <= 18)
            return classJobId is >= 16 and <= 18 ? JobRole.Gatherer : JobRole.Crafter;
        return sheetRole switch
        {
            1 => JobRole.Tank,
            2 => JobRole.Melee,
            3 => CasterJobs.Contains(classJobId) ? JobRole.Caster : JobRole.Ranged,
            4 => JobRole.Healer,
            _ => null,
        };
    }

    /// <summary> Discipline from the ClassJob row. </summary>
    public static Discipline DisciplineFor(uint classJobId, JobRole? role)
    {
        if (classJobId is >= 8 and <= 15) return Discipline.Hand;
        if (classJobId is >= 16 and <= 18) return Discipline.Land;
        return role switch
        {
            JobRole.Crafter  => Discipline.Hand,
            JobRole.Gatherer => Discipline.Land,
            JobRole.Caster   => Discipline.Magic,
            JobRole.Healer   => Discipline.Magic,
            _                => Discipline.War,
        };
    }

    /// <summary> Housing district by the TerritoryType place name (shared by the outdoor ward, houses, apartments and chambers). </summary>
    public static HousingDistrict? DistrictForPlaceName(string placeName)
        => placeName switch
        {
            "Mist"              => HousingDistrict.Mist,
            "The Lavender Beds" => HousingDistrict.LavenderBeds,
            "The Goblet"        => HousingDistrict.Goblet,
            "Shirogane"         => HousingDistrict.Shirogane,
            "Empyreum"          => HousingDistrict.Empyreum,
            _                   => null,
        };

    /// <summary> TerritoryIntendedUse row ids. </summary>
    public static class IntendedUse
    {
        public const byte Town          = 0;
        public const byte OpenWorld     = 1;
        public const byte InnRoom       = 2;
        public const byte Dungeon       = 3;
        public const byte HousingWard   = 13;
        public const byte HousingIndoor = 14;
        public const byte GoldSaucer    = 23;
        public const byte Barracks      = 30;
    }

    /// <summary> Maps a ContentType name (plus a few ContentFinderCondition flags) to a Lamina ContentKind. </summary>
    public static ContentKind ContentKindFor(string contentTypeName, bool isPvp, bool allianceRoulette, bool highEnd)
    {
        if (isPvp)
            return ContentKind.PvP;
        var n = contentTypeName;
        if (n.Contains("Deep Dungeon", StringComparison.OrdinalIgnoreCase)) return ContentKind.DeepDungeon;
        if (n.Contains("V&C", StringComparison.OrdinalIgnoreCase) || n.Contains("Variant", StringComparison.OrdinalIgnoreCase) || n.Contains("Criterion", StringComparison.OrdinalIgnoreCase)) return ContentKind.VariantCriterion;
        if (n.Contains("Dungeon", StringComparison.OrdinalIgnoreCase)) return ContentKind.Dungeon;
        if (n.Contains("Guildhest", StringComparison.OrdinalIgnoreCase)) return ContentKind.Guildhest;
        if (n.Contains("Trial", StringComparison.OrdinalIgnoreCase)) return ContentKind.Trial;
        if (n.Contains("Raid", StringComparison.OrdinalIgnoreCase)) return allianceRoulette ? ContentKind.AllianceRaid : ContentKind.Raid;
        if (n.Contains("PvP", StringComparison.OrdinalIgnoreCase)) return ContentKind.PvP;
        if (n.Contains("Quest", StringComparison.OrdinalIgnoreCase)) return ContentKind.QuestBattle;
        if (n.Contains("Treasure", StringComparison.OrdinalIgnoreCase)) return ContentKind.TreasureDungeon;
        if (n.Contains("Eureka", StringComparison.OrdinalIgnoreCase) || n.Contains("Save the Queen", StringComparison.OrdinalIgnoreCase)
         || n.Contains("Bozja", StringComparison.OrdinalIgnoreCase) || n.Contains("Occult", StringComparison.OrdinalIgnoreCase)
         || n.Contains("Field", StringComparison.OrdinalIgnoreCase) || n.Contains("Forays", StringComparison.OrdinalIgnoreCase)) return ContentKind.FieldOperation;
        if (n.Contains("Gold Saucer", StringComparison.OrdinalIgnoreCase)) return ContentKind.GoldSaucer;
        _ = highEnd;
        return ContentKind.Other;
    }

    /// <summary> One seasonal event with its approximate yearly window (month, day). Windows may wrap the year end. </summary>
    public sealed record SeasonalEvent(string Name, (int Month, int Day) From, (int Month, int Day) To);

    /// <summary> Approximate windows for the recurring seasonal events (TDD 13). Exact dates shift every year. </summary>
    public static readonly IReadOnlyList<SeasonalEvent> SeasonalEvents = new List<SeasonalEvent>
    {
        new("Heavensturn",          (12, 31), (1, 15)),
        new("Valentione's Day",     (2, 1),   (2, 15)),
        new("Little Ladies' Day",   (3, 1),   (3, 15)),
        new("Hatching-tide",        (3, 25),  (4, 8)),
        new("Make It Rain",         (6, 10),  (6, 27)),
        new("Moonfire Faire",       (8, 7),   (8, 26)),
        new("The Rising",           (8, 27),  (9, 11)),
        new("All Saints' Wake",     (10, 18), (11, 1)),
        new("Starlight Celebration",(12, 15), (12, 31)),
    };

    /// <summary> Names of the seasonal events active on <paramref name="date"/>. </summary>
    public static IReadOnlyList<string> ActiveSeasonalEvents(DateOnly date)
    {
        var result = new List<string>();
        var md = date.Month * 100 + date.Day;
        foreach (var ev in SeasonalEvents)
        {
            var from = ev.From.Month * 100 + ev.From.Day;
            var to   = ev.To.Month * 100 + ev.To.Day;
            var active = from <= to ? md >= from && md <= to : md >= from || md <= to;
            if (active)
                result.Add(ev.Name);
        }

        return result;
    }

    /// <summary> Biome names Lamina ships with. Users may add their own strings. </summary>
    public static class Biomes
    {
        public const string Desert       = "Desert";
        public const string Snow         = "Snow";
        public const string Forest       = "Forest";
        public const string Jungle       = "Jungle";
        public const string Coastal      = "Coastal";
        public const string Highland     = "Highland";
        public const string Underwater   = "Underwater";
        public const string Urban        = "Urban";
        public const string Lunar        = "Lunar";
        public const string Interior     = "Interior";
        public const string Otherworldly = "Otherworldly";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Desert, Snow, Forest, Jungle, Coastal, Highland, Underwater, Urban, Lunar, Interior, Otherworldly,
        };
    }

    /// <summary> Default territory → biome table for the main overworld zones and cities. User overrides in biomes.json win. </summary>
    public static readonly IReadOnlyDictionary<uint, string> DefaultBiomes = new Dictionary<uint, string>
    {
        // ---- cities ----
        [128] = Biomes.Urban,       // Limsa Lominsa Upper Decks
        [129] = Biomes.Urban,       // Limsa Lominsa Lower Decks
        [130] = Biomes.Urban,       // Ul'dah - Steps of Nald
        [131] = Biomes.Urban,       // Ul'dah - Steps of Thal
        [132] = Biomes.Urban,       // New Gridania
        [133] = Biomes.Urban,       // Old Gridania
        [418] = Biomes.Urban,       // Foundation
        [419] = Biomes.Urban,       // The Pillars
        [478] = Biomes.Urban,       // Idyllshire
        [628] = Biomes.Urban,       // Kugane
        [635] = Biomes.Urban,       // Rhalgr's Reach
        [819] = Biomes.Urban,       // The Crystarium
        [820] = Biomes.Urban,       // Eulmore
        [962] = Biomes.Urban,       // Old Sharlayan
        [963] = Biomes.Urban,       // Radz-at-Han
        [1185] = Biomes.Urban,      // Tuliyollal
        [1186] = Biomes.Urban,      // Solution Nine
        // ---- housing / interiors ----
        [339] = Biomes.Coastal,     // Mist
        [340] = Biomes.Forest,      // The Lavender Beds
        [341] = Biomes.Desert,      // The Goblet
        [641] = Biomes.Coastal,     // Shirogane
        [979] = Biomes.Snow,        // Empyreum
        // ---- La Noscea ----
        [134] = Biomes.Coastal,     // Middle La Noscea
        [135] = Biomes.Coastal,     // Lower La Noscea
        [137] = Biomes.Coastal,     // Eastern La Noscea
        [138] = Biomes.Coastal,     // Western La Noscea
        [139] = Biomes.Highland,    // Upper La Noscea
        [180] = Biomes.Coastal,     // Outer La Noscea
        // ---- Black Shroud ----
        [148] = Biomes.Forest,      // Central Shroud
        [152] = Biomes.Forest,      // East Shroud
        [153] = Biomes.Forest,      // South Shroud
        [154] = Biomes.Forest,      // North Shroud
        // ---- Thanalan ----
        [140] = Biomes.Desert,      // Western Thanalan
        [141] = Biomes.Desert,      // Central Thanalan
        [145] = Biomes.Desert,      // Eastern Thanalan
        [146] = Biomes.Desert,      // Southern Thanalan
        [147] = Biomes.Desert,      // Northern Thanalan
        // ---- Coerthas / Mor Dhona ----
        [155] = Biomes.Snow,        // Coerthas Central Highlands
        [397] = Biomes.Snow,        // Coerthas Western Highlands
        [156] = Biomes.Otherworldly,// Mor Dhona
        // ---- Heavensward ----
        [398] = Biomes.Highland,    // The Dravanian Forelands
        [399] = Biomes.Highland,    // The Dravanian Hinterlands
        [400] = Biomes.Highland,    // The Churning Mists
        [401] = Biomes.Highland,    // The Sea of Clouds
        [402] = Biomes.Otherworldly,// Azys Lla
        // ---- Stormblood ----
        [612] = Biomes.Highland,    // The Fringes
        [620] = Biomes.Highland,    // The Peaks
        [621] = Biomes.Desert,      // The Lochs
        [613] = Biomes.Coastal,     // The Ruby Sea
        [614] = Biomes.Forest,      // Yanxia
        [622] = Biomes.Highland,    // The Azim Steppe
        // ---- Shadowbringers ----
        [813] = Biomes.Forest,      // Lakeland
        [814] = Biomes.Coastal,     // Kholusia
        [815] = Biomes.Desert,      // Amh Araeng
        [816] = Biomes.Forest,      // Il Mheg
        [817] = Biomes.Jungle,      // The Rak'tika Greatwood
        [818] = Biomes.Underwater,  // The Tempest
        // ---- Endwalker ----
        [956] = Biomes.Forest,      // Labyrinthos
        [957] = Biomes.Desert,      // Thavnair
        [958] = Biomes.Snow,        // Garlemald
        [959] = Biomes.Lunar,       // Mare Lamentorum
        [960] = Biomes.Lunar,       // Ultima Thule
        [961] = Biomes.Otherworldly,// Elpis
        // ---- Dawntrail ----
        [1187] = Biomes.Jungle,     // Urqopacha
        [1188] = Biomes.Jungle,     // Kozama'uka
        [1189] = Biomes.Jungle,     // Yak T'el
        [1190] = Biomes.Desert,     // Shaaloani
        [1191] = Biomes.Otherworldly,// Heritage Found
        [1192] = Biomes.Otherworldly,// Living Memory
    };
}
