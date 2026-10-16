using System.Numerics;
using Lamina.Decals;
using Lamina.Models;

namespace Lamina.Tests.Persistence;

/// <summary> Temporary directory that is deleted on dispose. </summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lamina.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary> Fully populated model instances covering every condition and layer kind. </summary>
public static class Fixtures
{
    public static Profile FullProfile()
    {
        var profile = new Profile { Name = "Full", Enabled = true };
        profile.Targets = new List<Target> { Target.LocalPlayer(), new() { Kind = TargetKind.Character, Name = "Some One", WorldId = 33 } };

        var normal = new PassGroup { Name = "Normal", Conditions = new ConditionStack { Root = AllConditionsGroup() } };
        normal.Passes.Add(new Pass
        {
            Name          = "Everything",
            Conditions    = new ConditionStack { Root = new ConditionGroup { Op = GroupOp.Any, Negate = true, Children = { new TimeCondition { Clock = ClockKind.LT, FromMinutes = 60, ToMinutes = 120 } } } },
            Layers        = AllLayers(),
            MacroOverride = new ApplyMask { Equipment = EquipSlotFlags.Head | EquipSlotFlags.Body, Customization = CustomizeFlags.Hairstyle, Meta = false, Advanced = false, Mods = true },
        });
        normal.Passes.Add(new Pass { Name = "Empty" });

        var final = new PassGroup { Name = "Final", IsFinal = true, Priority = 5, Enabled = false };
        final.Passes.Add(new Pass { Name = "Sit", Conditions = new ConditionStack { Root = new EmoteCondition { Poses = { PoseKind.Sitting }, HoldSeconds = 0.25f, LingerSeconds = 2f } } });

        profile.PassGroups.Add(normal);
        profile.PassGroups.Add(final);
        return profile;
    }

    public static ConditionGroup AllConditionsGroup()
        => new()
        {
            Op = GroupOp.All,
            Children =
            {
                new TimeCondition { Clock = ClockKind.ET, FromMinutes = 18 * 60, ToMinutes = 6 * 60, HoldSeconds = 1.5f, LingerSeconds = 3f },
                new CalendarCondition { DateFrom = new DateOnly(2026, 1, 1), DateTo = new DateOnly(2026, 12, 31), Weekdays = { DayOfWeek.Monday, DayOfWeek.Sunday }, Event = "Starlight Celebration" },
                new WeatherCondition { WeatherIds = { 7, 8 }, Groups = { WeatherGroup.Wet, WeatherGroup.Stormy } },
                new BiomeCondition { Biomes = { "Desert", "Snow" } },
                new LocationCondition { TerritoryIds = { 132, 133 }, RegionIds = { 22 }, PlaceNameIds = { 40 }, WorldIds = { 33 }, Sanctuary = true, Indoors = false, InnRoom = true, HomeWorld = false },
                new DutyCondition { ContentTypes = { ContentKind.Dungeon, ContentKind.PvP }, ContentIds = { 1, 2 }, InFate = true },
                new HousingCondition { Where = HousingWhere.FcHouse, Districts = { HousingDistrict.Mist, HousingDistrict.Empyreum }, Ward = 3, Plot = 12, Inside = true },
                new SocialCondition { PartyState = PartyState.Alliance, MinSize = 2, MaxSize = 24, Players = { "Some One@Balmung" } },
                new CharacterCondition { Races = { 1, 4 }, Clans = { 7 }, Genders = { 1 }, Characters = { "Some One@Balmung" } },
                new JobCondition { JobIds = { 19, 24 }, Roles = { JobRole.Tank, JobRole.Healer }, Disciplines = { Discipline.War }, GearsetIds = { 0, 3 }, LevelMin = 50, LevelMax = 100, WeaponDrawn = true },
                new OnlineStatusCondition { StatusIds = { 22 } },
                new StatusEffectCondition { StatusIds = { 48, 49 }, MinStacks = 2 },
                new MovementCondition { States = { MovementState.Mounted, MovementState.Flying, MovementState.Diving } },
                new BodyStateCondition { States = { BodyState.InCombat, BodyState.Dead }, HpBelowPercent = 30, Negate = true },
                new EmoteCondition { EmoteIds = { 50, 52 }, LoopingOnly = true, Poses = { PoseKind.GroundSitting, PoseKind.Dozing }, Enabled = false },
                new ActivityCondition { Activities = { ActivityKind.Crafting, ActivityKind.GPose } },
                new ConditionGroup { Op = GroupOp.Any, Negate = true, Children = { new WeatherCondition { Groups = { WeatherGroup.Clear } } } },
            },
        };

    public static List<Layer> AllLayers()
        => new()
        {
            new VitrineDesignLayer { VitrineDesignId = Guid.NewGuid(), SettingsOverride = new() { ["Body options"] = new() { "Tre S chest" } }, SlotOverride = EquipSlotFlags.Body | EquipSlotFlags.Feet, ClearOtherSlotsOverride = true },
            new GlamourerLayer { DesignId = Guid.NewGuid(), Enabled = false },
            new PenumbraAdjustLayer
            {
                Mods =
                {
                    new ModState { ModDirectory = "Housekeeper Outfit", ModName = "Housekeeper Outfit", State = ModToggle.Enable, Priority = 3, Settings = new() { ["Default"] = new() { "Default" }, ["Body options"] = new() { "A", "B" } } },
                    new ModState { ModDirectory = "Other Mod", State = ModToggle.Disable },
                },
            },
            new HonorificLayer { Title = "Rain Walker", IsPrefix = true, Color = new Vector3(1f, 0.5f, 0.25f), Glow = new Vector3(0.1f, 0.2f, 0.3f) },
            new HonorificLayer { Clear = true },
            new MoodlesLayer { Add = { Guid.NewGuid() }, AddPresets = { Guid.NewGuid() }, Remove = { Guid.NewGuid(), Guid.NewGuid() } },
            new EquipmentLayer
            {
                Slots =
                {
                    [EquipSlot.Feet] = new SlotEdit { ItemId = NothingItems.IdFor(EquipSlot.Feet) },
                    [EquipSlot.Body] = new SlotEdit { ApplyStains = true, Stain1 = 5, Stain2 = 7, Crest = true },
                    [EquipSlot.Head] = new SlotEdit { ItemId = 10032 },
                },
                Bonus = { [BonusSlot.Glasses] = BonusItems.IdFor(159) },
                Meta  = { [MetaIndex.HatVisible] = false, [MetaIndex.WeaponVisible] = true },
            },
            new AppearanceLayer
            {
                Customize  = { [CustomizeIndex.HairColor] = 60, [CustomizeIndex.Highlights] = 128, [CustomizeIndex.FacialFeature4] = 8 },
                Wetness    = true,
                Parameters =
                {
                    ["SkinDiffuse"] = new AdvancedParameter { Color = new Vector3(0.9f, 0.8f, 0.7f) },
                    ["LipDiffuse"]  = new AdvancedParameter { Color = new Vector3(0.4f, 0.1f, 0.1f), Alpha = 0.6f },
                    ["MuscleTone"]  = new AdvancedParameter { Scalar = 0.75f },
                },
            },
            new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "Eve", State = null, Priority = null, Settings = new() { ["Skin"] = new() { "Glossy" } } } } },
            new CustomizePlusLayer { ProfileId = Guid.Parse("c0de0000-0000-0000-0000-00000000c0de"), ProfileName = "Tall Lalafell" },
            new SimpleHeelsLayer { Offset = 0.125f, Relative = false },
            new DecalLayer
            {
                ImageFile = "3f2a9c1e5b7d4e8f9a0b1c2d3e4f5a6b.png",
                Opacity   = 0.75f,
                Placement = new DecalPlacement
                {
                    Surface    = DecalSurface.Face,
                    Position   = new Vector3(0.012f, 1.534f, 0.087f),
                    Normal     = new Vector3(0.1f, 0.2f, 0.9747f),
                    Rotation   = 32.5f,
                    Size       = 0.08f,
                    Depth      = 0.03f,
                    Uv         = new Vector2(0.3125f, 0.625f),
                    MirroredUv = true,
                    PartName   = "Left cheek",
                },
            },
            new EquipmentCleanseLayer { Enabled = false },
        };
}
