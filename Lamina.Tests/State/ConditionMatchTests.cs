using Lamina.Models;
using Lamina.Runtime;
using Xunit;

namespace Lamina.Tests.State;

/// <summary> Instantaneous <see cref="Condition.Matches"/> checks for every first-build condition kind against hand-built snapshots. </summary>
public class ConditionMatchTests
{
    private static GameStateSnapshot Snap() => new()
    {
        LoggedIn            = true,
        PlayerName          = "Aha Dev",
        HomeWorldId         = 33,
        CurrentWorldId      = 33,
        PlayerNameWithWorld = "Aha Dev@Zodiark",
        RaceId              = 4,
        ClanId              = 7,
        Gender              = 1,
        TerritoryId         = 339,
        RegionPlaceNameId   = 22,
        ZonePlaceNameId     = 22,
        PlaceNameId         = 1409,
        IsSanctuary         = true,
        IsIndoors           = false,
        Housing             = new HousingInfo(HousingDistrict.Mist, 3, 12, true, true, false, false, false),
        Biome               = "Coastal",
        WeatherId           = 7,
        WeatherGroups       = new HashSet<WeatherGroup> { WeatherGroup.Wet },
        EorzeaTime          = new TimeSpan(22, 30, 0),
        LocalTime           = new DateTime(2026, 10, 15, 14, 5, 0),
        ServerTimeUtc       = new DateTime(2026, 10, 15, 11, 5, 0, DateTimeKind.Utc),
        ClassJobId          = 24,
        Role                = JobRole.Healer,
        Discipline          = Discipline.Magic,
        Level               = 100,
        GearsetId           = 4,
        OnlineStatusId      = 22,
        Statuses            = new[] { new StatusEntry(50, 1, 10f), new StatusEntry(48, 1, 1000f) },
        HpPercent           = 40,
        Movement            = new HashSet<MovementState> { MovementState.Mounted, MovementState.Flying },
        Body                = new HashSet<BodyState> { BodyState.InCombat },
        Activities          = new HashSet<ActivityKind>(),
        EmoteId             = 50,
        EmoteLooping        = true,
        Pose                = PoseKind.Sitting,
        PartySize           = 4,
        PartyMembers        = new[] { "Aha Dev@Zodiark", "Friend One@Odin" },
    };

    [Fact]
    public void Time_EorzeaWrapsMidnight()
    {
        var c = new TimeCondition { Clock = ClockKind.ET, FromMinutes = 18 * 60, ToMinutes = 6 * 60 };
        Assert.True(c.Matches(Snap()));
        Assert.False(c.Matches(Snap() with { EorzeaTime = new TimeSpan(12, 0, 0) }));
        var local = new TimeCondition { Clock = ClockKind.LT, FromMinutes = 14 * 60, ToMinutes = 15 * 60 };
        Assert.True(local.Matches(Snap()));
        var server = new TimeCondition { Clock = ClockKind.ST, FromMinutes = 11 * 60, ToMinutes = 11 * 60 + 10 };
        Assert.True(server.Matches(Snap()));
        Assert.False(server.Matches(Snap() with { ServerTimeUtc = new DateTime(2026, 10, 15, 11, 30, 0, DateTimeKind.Utc) }));
    }

    [Fact]
    public void Weather_ByGroupOrId()
    {
        Assert.True(new WeatherCondition { Groups = { WeatherGroup.Wet } }.Matches(Snap()));
        Assert.False(new WeatherCondition { Groups = { WeatherGroup.Cold } }.Matches(Snap()));
        Assert.True(new WeatherCondition { WeatherIds = { 7 } }.Matches(Snap()));
        Assert.True(new WeatherCondition().Matches(Snap()));
    }

    [Fact]
    public void Location_Fields()
    {
        Assert.True(new LocationCondition { TerritoryIds = { 339 } }.Matches(Snap()));
        Assert.False(new LocationCondition { TerritoryIds = { 340 } }.Matches(Snap()));
        Assert.True(new LocationCondition { Sanctuary = true }.Matches(Snap()));
        Assert.False(new LocationCondition { Indoors = true }.Matches(Snap()));
        Assert.True(new LocationCondition { RegionIds = { 22 } }.Matches(Snap()));
        Assert.True(new LocationCondition { HomeWorld = true }.Matches(Snap()));
        Assert.False(new LocationCondition { HomeWorld = true }.Matches(Snap() with { CurrentWorldId = 99 }));
    }

    [Fact]
    public void Housing_OwnHouse()
    {
        Assert.True(new HousingCondition { Where = HousingWhere.OwnHouse }.Matches(Snap()));
        Assert.False(new HousingCondition { Where = HousingWhere.FcHouse }.Matches(Snap()));
        Assert.True(new HousingCondition { Districts = { HousingDistrict.Mist }, Ward = 3, Plot = 12 }.Matches(Snap()));
        Assert.False(new HousingCondition { Districts = { HousingDistrict.Goblet } }.Matches(Snap()));
        Assert.False(new HousingCondition().Matches(Snap() with { Housing = null }));
        Assert.True(new HousingCondition { Where = HousingWhere.Other }.Matches(Snap() with { Housing = new HousingInfo(HousingDistrict.Mist, 1, 1, true, false, false, false, false) }));
    }

    [Fact]
    public void Job_RoleGearsetLevel()
    {
        Assert.True(new JobCondition { Roles = { JobRole.Healer } }.Matches(Snap()));
        Assert.False(new JobCondition { Roles = { JobRole.Tank } }.Matches(Snap()));
        Assert.True(new JobCondition { GearsetIds = { 4 } }.Matches(Snap()));
        Assert.True(new JobCondition { JobIds = { 24 }, LevelMin = 90 }.Matches(Snap()));
        Assert.False(new JobCondition { LevelMax = 50 }.Matches(Snap()));
        Assert.True(new JobCondition { Disciplines = { Discipline.Magic } }.Matches(Snap()));
        Assert.False(new JobCondition { WeaponDrawn = true }.Matches(Snap()));
    }

    [Fact]
    public void OnlineStatus_RolePlaying()
    {
        Assert.True(new OnlineStatusCondition { StatusIds = { 22 } }.Matches(Snap()));
        Assert.False(new OnlineStatusCondition { StatusIds = { 22 } }.Matches(Snap() with { OnlineStatusId = 0 }));
    }

    [Fact]
    public void StatusEffect_ByIdAndStacks()
    {
        Assert.True(new StatusEffectCondition { StatusIds = { 50 } }.Matches(Snap()));
        Assert.False(new StatusEffectCondition { StatusIds = { 50 }, MinStacks = 2 }.Matches(Snap()));
        Assert.False(new StatusEffectCondition { StatusIds = { 999 } }.Matches(Snap()));
    }

    [Fact]
    public void Movement_Mounted()
    {
        Assert.True(new MovementCondition { States = { MovementState.Mounted } }.Matches(Snap()));
        Assert.True(new MovementCondition { States = { MovementState.Flying } }.Matches(Snap()));
        Assert.False(new MovementCondition { States = { MovementState.Swimming } }.Matches(Snap()));
    }

    [Fact]
    public void BodyState_CombatDeadLowHp()
    {
        Assert.True(new BodyStateCondition { States = { BodyState.InCombat } }.Matches(Snap()));
        Assert.False(new BodyStateCondition { States = { BodyState.Dead } }.Matches(Snap()));
        Assert.True(new BodyStateCondition { HpBelowPercent = 50 }.Matches(Snap()));
        Assert.False(new BodyStateCondition { HpBelowPercent = 30 }.Matches(Snap()));
        Assert.False(new BodyStateCondition { States = { BodyState.LowHp } }.Matches(Snap()));
        Assert.True(new BodyStateCondition { States = { BodyState.LowHp } }.Matches(Snap() with { HpPercent = 10 }));
    }

    [Fact]
    public void Emote_PoseAndLooping()
    {
        Assert.True(new EmoteCondition { Poses = { PoseKind.Sitting } }.Matches(Snap()));
        Assert.False(new EmoteCondition { Poses = { PoseKind.Dozing } }.Matches(Snap()));
        Assert.True(new EmoteCondition { EmoteIds = { 50 }, LoopingOnly = true }.Matches(Snap()));
        Assert.False(new EmoteCondition { EmoteIds = { 50 }, LoopingOnly = true }.Matches(Snap() with { EmoteLooping = false }));
        Assert.True(new EmoteCondition().Matches(Snap()));
        Assert.False(new EmoteCondition().Matches(Snap() with { EmoteId = 0 }));
    }

    [Fact]
    public void Social_PartyState()
    {
        Assert.True(new SocialCondition { PartyState = PartyState.Party }.Matches(Snap()));
        Assert.False(new SocialCondition { PartyState = PartyState.Solo }.Matches(Snap()));
        Assert.True(new SocialCondition { Players = { "friend one@odin" } }.Matches(Snap()));
        Assert.True(new SocialCondition { MinSize = 2, MaxSize = 8 }.Matches(Snap()));
    }

    [Fact]
    public void Character_RaceClanName()
    {
        Assert.True(new CharacterCondition { Races = { 4 }, Clans = { 7 } }.Matches(Snap()));
        Assert.True(new CharacterCondition { Characters = { "aha dev@zodiark" } }.Matches(Snap()));
        Assert.False(new CharacterCondition { Genders = { 0 } }.Matches(Snap()));
    }

    [Fact]
    public void Biome_And_Activity_And_Duty()
    {
        Assert.True(new BiomeCondition { Biomes = { "coastal" } }.Matches(Snap()));
        Assert.False(new ActivityCondition().Matches(Snap()));
        Assert.True(new ActivityCondition { Activities = { ActivityKind.GPose } }.Matches(Snap() with { Activities = new HashSet<ActivityKind> { ActivityKind.GPose } }));
        Assert.False(new DutyCondition { ContentTypes = { ContentKind.AnyDuty } }.Matches(Snap()));
        Assert.True(new DutyCondition { ContentTypes = { ContentKind.Dungeon } }.Matches(Snap() with { InDuty = true, ContentKind = ContentKind.Dungeon }));
        Assert.True(new DutyCondition { InFate = true }.Matches(Snap() with { InFate = true }));
    }

    [Fact]
    public void Calendar_WeekdayAndEvent()
    {
        Assert.True(new CalendarCondition { Weekdays = { DayOfWeek.Thursday } }.Matches(Snap()));
        Assert.False(new CalendarCondition { Weekdays = { DayOfWeek.Monday } }.Matches(Snap()));
        Assert.True(new CalendarCondition { Event = "The Rising" }.Matches(Snap() with { ActiveEvents = new[] { "The Rising" } }));
        Assert.False(new CalendarCondition { DateTo = new DateOnly(2026, 1, 1) }.Matches(Snap()));
    }

    [Fact]
    public void RedrawUnsafe_DerivedFromState()
    {
        Assert.True(Snap().RedrawUnsafe);
        Assert.False((Snap() with { Movement = new HashSet<MovementState>(), Body = new HashSet<BodyState>() }).RedrawUnsafe);
    }
}
