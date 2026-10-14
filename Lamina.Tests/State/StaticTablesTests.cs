using Lamina.Models;
using Lamina.Runtime.State;
using Xunit;

namespace Lamina.Tests.State;

public class StaticTablesTests
{
    [Fact]
    public void WeatherGroups_KnownAndUnknown()
    {
        Assert.Contains(WeatherGroup.Wet, StaticTables.WeatherGroupsFor("Rain"));
        var storms = StaticTables.WeatherGroupsFor("Thunderstorms");
        Assert.Contains(WeatherGroup.Stormy, storms);
        Assert.Contains(WeatherGroup.Wet, storms);
        Assert.Contains(WeatherGroup.Clear, StaticTables.WeatherGroupsFor("fair skies"));
        Assert.Empty(StaticTables.WeatherGroupsFor("Nonsense"));
    }

    [Theory]
    [InlineData(19u, 1, -1, JobRole.Tank, Discipline.War)]      // PLD
    [InlineData(24u, 4, -1, JobRole.Healer, Discipline.Magic)]  // WHM
    [InlineData(22u, 2, -1, JobRole.Melee, Discipline.War)]     // DRG
    [InlineData(23u, 3, -1, JobRole.Ranged, Discipline.War)]    // BRD
    [InlineData(25u, 3, -1, JobRole.Caster, Discipline.Magic)]  // BLM
    [InlineData(42u, 3, -1, JobRole.Caster, Discipline.Magic)]  // PCT
    [InlineData(8u, 0, 0, JobRole.Crafter, Discipline.Hand)]    // CRP
    [InlineData(16u, 0, 8, JobRole.Gatherer, Discipline.Land)]  // MIN
    public void RoleAndDiscipline(uint jobId, byte sheetRole, int dohDol, JobRole expectedRole, Discipline expectedDiscipline)
    {
        var role = StaticTables.RoleFor(jobId, sheetRole, (sbyte)dohDol, 0);
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedDiscipline, StaticTables.DisciplineFor(jobId, role));
    }

    [Fact]
    public void ContentKind_Mapping()
    {
        Assert.Equal(ContentKind.Dungeon, StaticTables.ContentKindFor("Dungeons", false, false, false));
        Assert.Equal(ContentKind.DeepDungeon, StaticTables.ContentKindFor("Deep Dungeons", false, false, false));
        Assert.Equal(ContentKind.Raid, StaticTables.ContentKindFor("Raids", false, false, true));
        Assert.Equal(ContentKind.AllianceRaid, StaticTables.ContentKindFor("Raids", false, true, false));
        Assert.Equal(ContentKind.PvP, StaticTables.ContentKindFor("Anything", true, false, false));
        Assert.Equal(ContentKind.FieldOperation, StaticTables.ContentKindFor("Save the Queen", false, false, false));
        Assert.Equal(ContentKind.VariantCriterion, StaticTables.ContentKindFor("V&C Dungeon Finder", false, false, false));
        Assert.Equal(ContentKind.Other, StaticTables.ContentKindFor("Wondrous Tails", false, false, false));
    }

    [Fact]
    public void SeasonalEvents_WrapYearEnd()
    {
        Assert.Contains("Heavensturn", StaticTables.ActiveSeasonalEvents(new DateOnly(2026, 1, 3)));
        Assert.Contains("Heavensturn", StaticTables.ActiveSeasonalEvents(new DateOnly(2025, 12, 31)));
        Assert.Contains("The Rising", StaticTables.ActiveSeasonalEvents(new DateOnly(2026, 9, 1)));
        Assert.DoesNotContain("Heavensturn", StaticTables.ActiveSeasonalEvents(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void HousingDistrict_ByPlaceName()
    {
        Assert.Equal(HousingDistrict.LavenderBeds, StaticTables.DistrictForPlaceName("The Lavender Beds"));
        Assert.Null(StaticTables.DistrictForPlaceName("Limsa Lominsa"));
    }

    [Fact]
    public void DefaultBiomes_CoverMajorZones()
    {
        Assert.Equal(StaticTables.Biomes.Desert, StaticTables.DefaultBiomes[141]);
        Assert.Equal(StaticTables.Biomes.Snow, StaticTables.DefaultBiomes[155]);
        Assert.Equal(StaticTables.Biomes.Urban, StaticTables.DefaultBiomes[128]);
        Assert.All(StaticTables.DefaultBiomes.Values, v => Assert.Contains(v, StaticTables.Biomes.All));
    }
}
