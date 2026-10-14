using Lamina.Models;

namespace Lamina.Runtime;

/// <summary> One status effect on the target. </summary>
public readonly record struct StatusEntry(uint StatusId, int Stacks, float RemainingSeconds);

/// <summary> Housing details, present only while inside a housing district. </summary>
public sealed record HousingInfo(
    HousingDistrict? District,
    int Ward,
    int Plot,
    bool Inside,
    bool IsOwnHouse,
    bool IsFcHouse,
    bool IsApartment,
    bool IsChambers);

/// <summary>
/// Everything the condition catalog reads, for one target, frozen at one point in time.
/// Built by the StateTracker only when something relevant changed. Contains no Dalamud types so it can be built by hand in tests.
/// All collections are non-null (empty when unknown).
/// </summary>
public sealed record GameStateSnapshot
{
    public static readonly GameStateSnapshot Empty = new();

    public DateTime BuiltAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary> Monotonic counter, bumps with every new snapshot. </summary>
    public long Sequence { get; init; }

    // ---- identity -------------------------------------------------------------------------------
    public bool LoggedIn { get; init; }
    public string PlayerName { get; init; } = string.Empty;
    public uint HomeWorldId { get; init; }
    public uint CurrentWorldId { get; init; }
    public string PlayerNameWithWorld { get; init; } = string.Empty;
    public byte RaceId { get; init; }
    public byte ClanId { get; init; }
    /// <summary> 0 = male, 1 = female (game convention). </summary>
    public byte Gender { get; init; }

    // ---- place ----------------------------------------------------------------------------------
    public uint TerritoryId { get; init; }
    public uint MapId { get; init; }
    public byte TerritoryIntendedUse { get; init; }
    /// <summary> TerritoryType.PlaceNameRegion row id (e.g. "La Noscea"). </summary>
    public uint RegionPlaceNameId { get; init; }
    /// <summary> TerritoryType.PlaceNameZone row id. </summary>
    public uint ZonePlaceNameId { get; init; }
    /// <summary> TerritoryType.PlaceName row id (the zone's own name). </summary>
    public uint PlaceNameId { get; init; }
    public bool IsSanctuary { get; init; }
    public bool IsIndoors { get; init; }
    public bool IsInnRoom { get; init; }
    public HousingInfo? Housing { get; init; }
    public string? Biome { get; init; }

    // ---- content --------------------------------------------------------------------------------
    public bool InDuty { get; init; }
    public uint ContentFinderConditionId { get; init; }
    public ContentKind ContentKind { get; init; } = ContentKind.Other;
    public bool InFate { get; init; }

    // ---- world ----------------------------------------------------------------------------------
    public byte WeatherId { get; init; }
    public IReadOnlySet<WeatherGroup> WeatherGroups { get; init; } = new HashSet<WeatherGroup>();
    /// <summary> Time of day in Eorzea, 0 ≤ t &lt; 24h. </summary>
    public TimeSpan EorzeaTime { get; init; }
    public DateTime LocalTime { get; init; } = DateTime.Now;
    public DateTime ServerTimeUtc { get; init; } = DateTime.UtcNow;
    /// <summary> Seasonal event names currently active (from the Calendar catalog). </summary>
    public IReadOnlyList<string> ActiveEvents { get; init; } = Array.Empty<string>();

    // ---- job and gear ---------------------------------------------------------------------------
    public uint ClassJobId { get; init; }
    public JobRole? Role { get; init; }
    public Discipline? Discipline { get; init; }
    public int Level { get; init; }
    /// <summary> Current gearset id (0-based gearset index as shown in game minus one, or -1 when unknown). </summary>
    public int GearsetId { get; init; } = -1;
    public bool WeaponDrawn { get; init; }

    // ---- status ---------------------------------------------------------------------------------
    public uint OnlineStatusId { get; init; }
    public IReadOnlyList<StatusEntry> Statuses { get; init; } = Array.Empty<StatusEntry>();
    public int HpPercent { get; init; } = 100;

    // ---- state ----------------------------------------------------------------------------------
    public IReadOnlySet<MovementState> Movement { get; init; } = new HashSet<MovementState>();
    public IReadOnlySet<BodyState> Body { get; init; } = new HashSet<BodyState>();
    public IReadOnlySet<ActivityKind> Activities { get; init; } = new HashSet<ActivityKind>();
    public ushort EmoteId { get; init; }
    public bool EmoteLooping { get; init; }
    public PoseKind? Pose { get; init; }

    // ---- social ---------------------------------------------------------------------------------
    /// <summary> 1 when solo. </summary>
    public int PartySize { get; init; } = 1;
    public bool InAlliance { get; init; }
    public bool CrossWorldParty { get; init; }
    /// <summary> "Name@World" of every party member including the player. </summary>
    public IReadOnlyList<string> PartyMembers { get; init; } = Array.Empty<string>();

    // ---- raw ------------------------------------------------------------------------------------
    /// <summary> Raw Dalamud ConditionFlag values that are set, for the debug overlay and /lam why. </summary>
    public IReadOnlySet<int> RawConditionFlags { get; init; } = new HashSet<int>();

    /// <summary> True when a redraw right now would be jarring (combat, cutscene, loading, mounted / flying, GPose). </summary>
    public bool RedrawUnsafe
        => Body.Contains(BodyState.InCombat) || Body.Contains(BodyState.Loading)
         || Activities.Contains(ActivityKind.Cutscene) || Activities.Contains(ActivityKind.GPose)
         || Movement.Contains(MovementState.Mounted) || Movement.Contains(MovementState.Flying);
}
