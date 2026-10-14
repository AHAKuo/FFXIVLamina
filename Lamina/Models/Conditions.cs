using Lamina.Runtime;

namespace Lamina.Models;

/// <summary> A tree of conditions. An empty stack (Root == null) is always true. </summary>
public sealed class ConditionStack
{
    public ConditionNode? Root { get; set; }

    public bool IsEmpty
        => Root is null || (Root is ConditionGroup g && g.Children.Count == 0 && !g.Negate);

    /// <summary> Enumerates every leaf condition in the tree. </summary>
    public IEnumerable<Condition> Leaves()
        => Root is null ? Enumerable.Empty<Condition>() : Root.Leaves();

    /// <summary> Returns the root as a group, creating an All group if the root is empty or a leaf. Used by the UI. </summary>
    public ConditionGroup EnsureRootGroup()
    {
        switch (Root)
        {
            case ConditionGroup g: return g;
            case Condition c:
                var wrapped = new ConditionGroup { Children = { c } };
                Root = wrapped;
                return wrapped;
            default:
                var fresh = new ConditionGroup();
                Root = fresh;
                return fresh;
        }
    }
}

/// <summary> Base of the condition tree. Serialized polymorphically by "kind" ("Group" for groups, the condition kind for leaves). </summary>
public abstract class ConditionNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Negate { get; set; }

    public abstract IEnumerable<Condition> Leaves();
}

public sealed class ConditionGroup : ConditionNode
{
    public const string KindName = "Group";

    public GroupOp Op { get; set; } = GroupOp.All;

    public List<ConditionNode> Children { get; set; } = new();

    public override IEnumerable<Condition> Leaves()
        => Children.SelectMany(c => c.Leaves());
}

/// <summary>
/// A leaf condition. Subclasses are serialized with a "kind" field equal to <see cref="Kind"/>.
/// <see cref="Matches"/> is the instantaneous check (no hold / linger); the evaluator wraps it with timers.
/// </summary>
public abstract class Condition : ConditionNode
{
    public bool Enabled { get; set; } = true;

    /// <summary> Seconds the condition must be true before it counts. Null = use the config default. </summary>
    public float? HoldSeconds { get; set; }

    /// <summary> Seconds the condition stays true after it stopped. Null = use the config default. </summary>
    public float? LingerSeconds { get; set; }

    /// <summary> The "kind" discriminator used in JSON. Stable, never renamed. </summary>
    public abstract string Kind { get; }

    /// <summary>
    /// Transient kinds (movement, body state, emote, activity, status effects) flicker and get the config's default hold / linger.
    /// Stable kinds (time, place, job, ...) use 0 unless the condition sets HoldSeconds / LingerSeconds explicitly.
    /// </summary>
    public virtual bool IsTransient => false;

    /// <summary> Instantaneous evaluation against a snapshot, ignoring Negate / Enabled / timers. </summary>
    public abstract bool Matches(GameStateSnapshot s);

    /// <summary> One line summary for the UI ("Weather: Rain, Showers"). </summary>
    public abstract string Describe();

    public override IEnumerable<Condition> Leaves()
    {
        yield return this;
    }
}

/// <summary> Kept when a file from a newer version contains a kind this build does not know. Never matches. </summary>
public sealed class UnknownCondition : Condition
{
    public override string Kind => UnknownKind;

    public string UnknownKind { get; set; } = "Unknown";

    /// <summary> The raw JSON so saving does not lose it. </summary>
    public string RawJson { get; set; } = "{}";

    public override bool Matches(GameStateSnapshot s) => false;

    public override string Describe() => $"Unsupported condition ({UnknownKind})";
}

// ----------------------------------------------------------------------------------------------------
// Condition kinds. Fields follow TDD section 8.2. Empty lists mean "any".
// ----------------------------------------------------------------------------------------------------

public enum ClockKind
{
    /// <summary> Eorzea time. </summary>
    ET,
    /// <summary> Local (real) time. </summary>
    LT,
    /// <summary> Server time (UTC). </summary>
    ST,
}

public sealed class TimeCondition : Condition
{
    public const string KindName = "Time";
    public override string Kind => KindName;

    public ClockKind Clock { get; set; } = ClockKind.ET;

    /// <summary> Minutes since midnight, inclusive. Ranges may wrap midnight (From &gt; To). </summary>
    public int FromMinutes { get; set; } = 18 * 60;

    /// <summary> Minutes since midnight, exclusive. </summary>
    public int ToMinutes { get; set; } = 6 * 60;

    public override bool Matches(GameStateSnapshot s)
    {
        var minutes = Clock switch
        {
            ClockKind.ET => (int)s.EorzeaTime.TotalMinutes % 1440,
            ClockKind.LT => s.LocalTime.Hour * 60 + s.LocalTime.Minute,
            _            => s.ServerTimeUtc.Hour * 60 + s.ServerTimeUtc.Minute,
        };
        return InRange(minutes, FromMinutes, ToMinutes);
    }

    public static bool InRange(int value, int from, int to)
    {
        if (from == to)
            return true;
        return from < to ? value >= from && value < to : value >= from || value < to;
    }

    public override string Describe()
        => $"Time ({Clock}): {FromMinutes / 60:00}:{FromMinutes % 60:00} – {ToMinutes / 60:00}:{ToMinutes % 60:00}";
}

public sealed class CalendarCondition : Condition
{
    public const string KindName = "Calendar";
    public override string Kind => KindName;

    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public List<DayOfWeek> Weekdays { get; set; } = new();
    public string? Event { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        var today = DateOnly.FromDateTime(s.LocalTime);
        if (DateFrom is { } from && today < from) return false;
        if (DateTo is { } to && today > to) return false;
        if (Weekdays.Count > 0 && !Weekdays.Contains(s.LocalTime.DayOfWeek)) return false;
        if (!string.IsNullOrEmpty(Event) && !s.ActiveEvents.Contains(Event, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    public override string Describe()
        => "Calendar";
}

/// <summary> Lamina defined weather groups. The catalog maps weather ids to groups. </summary>
public enum WeatherGroup
{
    Clear,
    Overcast,
    Wet,
    Stormy,
    Windy,
    Hot,
    Cold,
    Arcane,
}

public sealed class WeatherCondition : Condition
{
    public const string KindName = "Weather";
    public override string Kind => KindName;

    public List<byte> WeatherIds { get; set; } = new();
    public List<WeatherGroup> Groups { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
    {
        if (WeatherIds.Count == 0 && Groups.Count == 0)
            return true;
        if (WeatherIds.Contains(s.WeatherId))
            return true;
        return Groups.Count > 0 && Groups.Any(g => s.WeatherGroups.Contains(g));
    }

    public override string Describe()
        => $"Weather: {string.Join(", ", Groups.Select(g => g.ToString()).Concat(WeatherIds.Select(i => $"#{i}")))}";
}

public sealed class BiomeCondition : Condition
{
    public const string KindName = "Biome";
    public override string Kind => KindName;

    public List<string> Biomes { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
        => Biomes.Count == 0 || (s.Biome is not null && Biomes.Contains(s.Biome, StringComparer.OrdinalIgnoreCase));

    public override string Describe()
        => $"Biome: {string.Join(", ", Biomes)}";
}

public sealed class LocationCondition : Condition
{
    public const string KindName = "Location";
    public override string Kind => KindName;

    public List<uint> TerritoryIds { get; set; } = new();
    public List<uint> RegionIds { get; set; } = new();
    public List<uint> PlaceNameIds { get; set; } = new();
    public List<uint> WorldIds { get; set; } = new();
    public bool? Sanctuary { get; set; }
    public bool? Indoors { get; set; }
    public bool? InnRoom { get; set; }
    public bool? HomeWorld { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (TerritoryIds.Count > 0 && !TerritoryIds.Contains(s.TerritoryId)) return false;
        if (RegionIds.Count > 0 && !RegionIds.Contains(s.RegionPlaceNameId)) return false;
        if (PlaceNameIds.Count > 0 && !PlaceNameIds.Contains(s.PlaceNameId) && !PlaceNameIds.Contains(s.ZonePlaceNameId)) return false;
        if (WorldIds.Count > 0 && !WorldIds.Contains(s.CurrentWorldId)) return false;
        if (Sanctuary is { } sanc && s.IsSanctuary != sanc) return false;
        if (Indoors is { } ind && s.IsIndoors != ind) return false;
        if (InnRoom is { } inn && s.IsInnRoom != inn) return false;
        if (HomeWorld is { } home && (s.CurrentWorldId == s.HomeWorldId) != home) return false;
        return true;
    }

    public override string Describe()
        => "Location";
}

public enum ContentKind
{
    AnyDuty,
    Dungeon,
    Trial,
    Raid,
    AllianceRaid,
    Guildhest,
    QuestBattle,
    TreasureDungeon,
    DeepDungeon,
    FieldOperation,
    VariantCriterion,
    PvP,
    GoldSaucer,
    Other,
}

public sealed class DutyCondition : Condition
{
    public const string KindName = "Duty";
    public override string Kind => KindName;

    public List<ContentKind> ContentTypes { get; set; } = new();
    public List<uint> ContentIds { get; set; } = new();
    public bool? InFate { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (InFate is { } fate && s.InFate != fate) return false;
        if (ContentIds.Count > 0 && !ContentIds.Contains(s.ContentFinderConditionId)) return false;
        if (ContentTypes.Count > 0)
        {
            if (!s.InDuty) return false;
            if (!ContentTypes.Contains(ContentKind.AnyDuty) && !ContentTypes.Contains(s.ContentKind)) return false;
        }
        return true;
    }

    public override string Describe()
        => "Duty";
}

public enum HousingWhere
{
    Any,
    OwnHouse,
    FcHouse,
    Apartment,
    Chambers,
    Other,
}

public enum HousingDistrict
{
    Mist,
    LavenderBeds,
    Goblet,
    Shirogane,
    Empyreum,
}

public sealed class HousingCondition : Condition
{
    public const string KindName = "Housing";
    public override string Kind => KindName;

    public HousingWhere Where { get; set; } = HousingWhere.Any;
    public List<HousingDistrict> Districts { get; set; } = new();
    public int? Ward { get; set; }
    public int? Plot { get; set; }
    public bool? Inside { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (s.Housing is not { } h) return false;
        if (Districts.Count > 0 && (h.District is not { } d || !Districts.Contains(d))) return false;
        if (Ward is { } w && h.Ward != w) return false;
        if (Plot is { } p && h.Plot != p) return false;
        if (Inside is { } ins && h.Inside != ins) return false;
        return Where switch
        {
            HousingWhere.Any       => true,
            HousingWhere.OwnHouse  => h.Inside && h.IsOwnHouse,
            HousingWhere.FcHouse   => h.Inside && h.IsFcHouse,
            HousingWhere.Apartment => h.Inside && h.IsApartment,
            HousingWhere.Chambers  => h.Inside && h.IsChambers,
            HousingWhere.Other     => h.Inside && !h.IsOwnHouse && !h.IsFcHouse && !h.IsApartment && !h.IsChambers,
            _                      => false,
        };
    }

    public override string Describe()
        => $"Housing: {Where}";
}

public enum PartyState
{
    Any,
    Solo,
    Party,
    Alliance,
}

public sealed class SocialCondition : Condition
{
    public const string KindName = "Social";
    public override string Kind => KindName;

    public PartyState PartyState { get; set; } = PartyState.Any;
    public int? MinSize { get; set; }
    public int? MaxSize { get; set; }
    /// <summary> "Name@World" entries; any present in the party matches. </summary>
    public List<string> Players { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
    {
        switch (PartyState)
        {
            case PartyState.Solo when s.PartySize > 1:                   return false;
            case PartyState.Party when s.PartySize <= 1 || s.InAlliance: return false;
            case PartyState.Alliance when !s.InAlliance:                 return false;
        }
        if (MinSize is { } min && s.PartySize < min) return false;
        if (MaxSize is { } max && s.PartySize > max) return false;
        if (Players.Count > 0 && !Players.Any(p => s.PartyMembers.Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    public override string Describe()
        => $"Social: {PartyState}";
}

public sealed class CharacterCondition : Condition
{
    public const string KindName = "Character";
    public override string Kind => KindName;

    public List<byte> Races { get; set; } = new();
    public List<byte> Clans { get; set; } = new();
    public List<byte> Genders { get; set; } = new();
    /// <summary> "Name@World" entries. </summary>
    public List<string> Characters { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
    {
        if (Races.Count > 0 && !Races.Contains(s.RaceId)) return false;
        if (Clans.Count > 0 && !Clans.Contains(s.ClanId)) return false;
        if (Genders.Count > 0 && !Genders.Contains(s.Gender)) return false;
        if (Characters.Count > 0 && !Characters.Contains(s.PlayerNameWithWorld, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    public override string Describe()
        => "Character";
}

public enum JobRole
{
    Tank,
    Healer,
    Melee,
    Ranged,
    Caster,
    Crafter,
    Gatherer,
}

public enum Discipline
{
    War,
    Magic,
    Hand,
    Land,
}

public sealed class JobCondition : Condition
{
    public const string KindName = "Job";
    public override string Kind => KindName;

    public List<uint> JobIds { get; set; } = new();
    public List<JobRole> Roles { get; set; } = new();
    public List<Discipline> Disciplines { get; set; } = new();
    public List<int> GearsetIds { get; set; } = new();
    public int? LevelMin { get; set; }
    public int? LevelMax { get; set; }
    public bool? WeaponDrawn { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (JobIds.Count > 0 && !JobIds.Contains(s.ClassJobId)) return false;
        if (Roles.Count > 0 && (s.Role is not { } r || !Roles.Contains(r))) return false;
        if (Disciplines.Count > 0 && (s.Discipline is not { } d || !Disciplines.Contains(d))) return false;
        if (GearsetIds.Count > 0 && !GearsetIds.Contains(s.GearsetId)) return false;
        if (LevelMin is { } min && s.Level < min) return false;
        if (LevelMax is { } max && s.Level > max) return false;
        if (WeaponDrawn is { } drawn && s.WeaponDrawn != drawn) return false;
        return true;
    }

    public override string Describe()
        => "Job";
}

public sealed class OnlineStatusCondition : Condition
{
    public const string KindName = "OnlineStatus";
    public override string Kind => KindName;

    /// <summary> OnlineStatus sheet row ids (Role-playing is 22). </summary>
    public List<uint> StatusIds { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
        => StatusIds.Count == 0 || StatusIds.Contains(s.OnlineStatusId);

    public override string Describe()
        => $"Online status: {string.Join(", ", StatusIds)}";
}

public sealed class StatusEffectCondition : Condition
{
    public const string KindName = "StatusEffect";
    public override string Kind => KindName;
    public override bool IsTransient => true;

    public List<uint> StatusIds { get; set; } = new();
    public int? MinStacks { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (StatusIds.Count == 0)
            return s.Statuses.Count > 0;
        return s.Statuses.Any(st => StatusIds.Contains(st.StatusId) && (MinStacks is not { } min || st.Stacks >= min));
    }

    public override string Describe()
        => "Status effect";
}

public enum MovementState
{
    Idle,
    Walking,
    Running,
    Sprinting,
    Jumping,
    Falling,
    Mounted,
    RidingPillion,
    Flying,
    Swimming,
    Diving,
    BeingMoved,
}

public sealed class MovementCondition : Condition
{
    public const string KindName = "Movement";
    public override string Kind => KindName;
    public override bool IsTransient => true;

    public List<MovementState> States { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
        => States.Count == 0 || States.Any(st => s.Movement.Contains(st));

    public override string Describe()
        => $"Movement: {string.Join(", ", States)}";
}

public enum BodyState
{
    Dead,
    InCombat,
    Casting,
    Wet,
    WeaponDrawn,
    Transformed,
    Disguised,
    Loading,
    LowHp,
}

public sealed class BodyStateCondition : Condition
{
    public const string KindName = "BodyState";
    public override string Kind => KindName;
    public override bool IsTransient => true;

    public List<BodyState> States { get; set; } = new();
    public int? HpBelowPercent { get; set; }

    public override bool Matches(GameStateSnapshot s)
    {
        if (HpBelowPercent is { } hp && s.HpPercent >= hp) return false;
        if (States.Count == 0) return true;
        return States.Any(st => st switch
        {
            BodyState.LowHp => HpBelowPercent is null ? s.HpPercent < 25 : true,
            _               => s.Body.Contains(st),
        });
    }

    public override string Describe()
        => $"Body: {string.Join(", ", States)}";
}

public enum PoseKind
{
    Sitting,
    GroundSitting,
    Dozing,
    Standing,
}

public sealed class EmoteCondition : Condition
{
    public const string KindName = "Emote";
    public override string Kind => KindName;
    public override bool IsTransient => true;

    public List<ushort> EmoteIds { get; set; } = new();
    public bool LoopingOnly { get; set; }
    public List<PoseKind> Poses { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
    {
        if (Poses.Count > 0 && (s.Pose is not { } pose || !Poses.Contains(pose)))
            return false;
        if (EmoteIds.Count > 0 && !EmoteIds.Contains(s.EmoteId))
            return false;
        if (EmoteIds.Count == 0 && Poses.Count == 0 && s.EmoteId == 0)
            return false;
        if (LoopingOnly && !s.EmoteLooping)
            return false;
        return true;
    }

    public override string Describe()
        => $"Emote: {string.Join(", ", Poses.Select(p => p.ToString()).Concat(EmoteIds.Select(i => $"#{i}")))}";
}

public enum ActivityKind
{
    Crafting,
    Gathering,
    Fishing,
    Performing,
    NpcDialogue,
    Cutscene,
    GPose,
    Trading,
    MiniGame,
    RetainerOrMarket,
    MinionSummoned,
}

public sealed class ActivityCondition : Condition
{
    public const string KindName = "Activity";
    public override string Kind => KindName;
    public override bool IsTransient => true;

    public List<ActivityKind> Activities { get; set; } = new();

    public override bool Matches(GameStateSnapshot s)
        => Activities.Count == 0 ? s.Activities.Count > 0 : Activities.Any(a => s.Activities.Contains(a));

    public override string Describe()
        => $"Activity: {string.Join(", ", Activities)}";
}

/// <summary> Registry of every condition kind this build knows. Used by the JSON converter and the Add Condition menu. </summary>
public static class ConditionKinds
{
    public static readonly IReadOnlyList<(string Kind, string DisplayName, Func<Condition> Create)> All = new List<(string, string, Func<Condition>)>
    {
        (TimeCondition.KindName,         "Time",          () => new TimeCondition()),
        (CalendarCondition.KindName,     "Calendar",      () => new CalendarCondition()),
        (WeatherCondition.KindName,      "Weather",       () => new WeatherCondition()),
        (BiomeCondition.KindName,        "Biome",         () => new BiomeCondition()),
        (LocationCondition.KindName,     "Location",      () => new LocationCondition()),
        (DutyCondition.KindName,         "Duty",          () => new DutyCondition()),
        (HousingCondition.KindName,      "Housing",       () => new HousingCondition()),
        (SocialCondition.KindName,       "Social",        () => new SocialCondition()),
        (CharacterCondition.KindName,    "Character",     () => new CharacterCondition()),
        (JobCondition.KindName,          "Job",           () => new JobCondition()),
        (OnlineStatusCondition.KindName, "Online Status", () => new OnlineStatusCondition()),
        (StatusEffectCondition.KindName, "Status Effect", () => new StatusEffectCondition()),
        (MovementCondition.KindName,     "Movement",      () => new MovementCondition()),
        (BodyStateCondition.KindName,    "Body State",    () => new BodyStateCondition()),
        (EmoteCondition.KindName,        "Emote / Pose",  () => new EmoteCondition()),
        (ActivityCondition.KindName,     "Activity",      () => new ActivityCondition()),
    };

    public static Condition? Create(string kind)
    {
        foreach (var (k, _, create) in All)
            if (k == kind)
                return create();
        return null;
    }
}
