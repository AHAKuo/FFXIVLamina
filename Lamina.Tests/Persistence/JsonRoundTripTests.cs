using Lamina.Models;
using Lamina.Persistence;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Persistence;

public class JsonRoundTripTests
{
    [Fact]
    public void FullProfile_RoundTrips_Losslessly()
    {
        var original = Fixtures.FullProfile();
        var json     = JsonSettings.Serialize(original);
        var reloaded = JsonSettings.Deserialize<Profile>(json);
        var again    = JsonSettings.Serialize(reloaded);

        Assert.True(JToken.DeepEquals(JToken.Parse(json), JToken.Parse(again)), $"Round trip changed the JSON:\n{json}\n---\n{again}");
        Assert.Equal(original.Id, reloaded.Id);
        Assert.Equal(2, reloaded.Targets.Count);
        Assert.Equal(TargetKind.Character, reloaded.Targets[1].Kind);
        Assert.Equal("Some One", reloaded.Targets[1].Name);
        Assert.Equal(33u, reloaded.Targets[1].WorldId);
        Assert.Equal(2, reloaded.PassGroups.Count);
        Assert.True(reloaded.PassGroups[1].IsFinal);
        Assert.Equal(5, reloaded.PassGroups[1].Priority);
        Assert.False(reloaded.PassGroups[1].Enabled);
    }

    [Fact]
    public void EveryConditionKind_RoundTrips_WithAllFields()
    {
        var group    = Fixtures.AllConditionsGroup();
        var json     = JsonSettings.Serialize(group);
        var reloaded = (ConditionGroup)JsonSettings.Deserialize<ConditionNode>(json);

        Assert.Equal(group.Children.Count, reloaded.Children.Count);
        foreach (var (expected, actual) in group.Children.Zip(reloaded.Children))
            Assert.Equal(expected.GetType(), actual.GetType());

        var kindsCovered = reloaded.Children.OfType<Condition>().Select(c => c.Kind).ToHashSet();
        foreach (var (kind, _, _) in ConditionKinds.All)
            Assert.Contains(kind, kindsCovered);

        var time = Assert.IsType<TimeCondition>(reloaded.Children[0]);
        Assert.Equal(ClockKind.ET, time.Clock);
        Assert.Equal(18 * 60, time.FromMinutes);
        Assert.Equal(6 * 60, time.ToMinutes);
        Assert.Equal(1.5f, time.HoldSeconds);
        Assert.Equal(3f, time.LingerSeconds);

        var calendar = Assert.IsType<CalendarCondition>(reloaded.Children[1]);
        Assert.Equal(new DateOnly(2026, 1, 1), calendar.DateFrom);
        Assert.Equal(new DateOnly(2026, 12, 31), calendar.DateTo);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Sunday }, calendar.Weekdays);
        Assert.Equal("Starlight Celebration", calendar.Event);

        var weather = Assert.IsType<WeatherCondition>(reloaded.Children[2]);
        Assert.Equal(new byte[] { 7, 8 }, weather.WeatherIds);
        Assert.Equal(new[] { WeatherGroup.Wet, WeatherGroup.Stormy }, weather.Groups);

        var location = Assert.IsType<LocationCondition>(reloaded.Children[4]);
        Assert.True(location.Sanctuary);
        Assert.False(location.Indoors);
        Assert.True(location.InnRoom);
        Assert.False(location.HomeWorld);
        Assert.Equal(new uint[] { 132, 133 }, location.TerritoryIds);

        var housing = Assert.IsType<HousingCondition>(reloaded.Children[6]);
        Assert.Equal(HousingWhere.FcHouse, housing.Where);
        Assert.Equal(3, housing.Ward);
        Assert.Equal(12, housing.Plot);
        Assert.True(housing.Inside);

        var job = Assert.IsType<JobCondition>(reloaded.Children[9]);
        Assert.Equal(new[] { JobRole.Tank, JobRole.Healer }, job.Roles);
        Assert.Equal(new[] { 0, 3 }, job.GearsetIds);
        Assert.Equal(50, job.LevelMin);
        Assert.True(job.WeaponDrawn);

        var body = Assert.IsType<BodyStateCondition>(reloaded.Children[13]);
        Assert.True(body.Negate);
        Assert.Equal(30, body.HpBelowPercent);

        var emote = Assert.IsType<EmoteCondition>(reloaded.Children[14]);
        Assert.False(emote.Enabled);
        Assert.True(emote.LoopingOnly);
        Assert.Equal(new ushort[] { 50, 52 }, emote.EmoteIds);

        var nested = Assert.IsType<ConditionGroup>(reloaded.Children[16]);
        Assert.Equal(GroupOp.Any, nested.Op);
        Assert.True(nested.Negate);
        Assert.Single(nested.Children);
    }

    [Fact]
    public void EveryLayerKind_RoundTrips_WithAllFields()
    {
        var layers   = Fixtures.AllLayers();
        var json     = JsonSettings.Serialize(layers);
        var reloaded = JsonSettings.Deserialize<List<Layer>>(json);

        Assert.Equal(layers.Count, reloaded.Count);
        var kindsCovered = reloaded.Select(l => l.Kind).ToHashSet();
        foreach (var (kind, _, _) in LayerKinds.All)
            Assert.Contains(kind, kindsCovered);

        var vitrine = Assert.IsType<VitrineDesignLayer>(reloaded[0]);
        Assert.Equal(((VitrineDesignLayer)layers[0]).VitrineDesignId, vitrine.VitrineDesignId);
        Assert.Equal(new[] { "Tre S chest" }, vitrine.SettingsOverride!["Body options"]);

        var glam = Assert.IsType<GlamourerLayer>(reloaded[1]);
        Assert.False(glam.Enabled);
        Assert.Equal(((GlamourerLayer)layers[1]).DesignId, glam.DesignId);

        var adjust = Assert.IsType<PenumbraAdjustLayer>(reloaded[2]);
        Assert.Equal(2, adjust.Mods.Count);
        Assert.Equal(3, adjust.Mods[0].Priority);
        Assert.Equal(new[] { "A", "B" }, adjust.Mods[0].Settings!["Body options"]);
        Assert.Equal(ModToggle.Disable, adjust.Mods[1].State);
        Assert.Null(adjust.Mods[1].Settings);
        Assert.Null(adjust.Mods[1].Priority);

        var title = Assert.IsType<HonorificLayer>(reloaded[3]);
        Assert.Equal("Rain Walker", title.Title);
        Assert.True(title.IsPrefix);
        Assert.Equal(new System.Numerics.Vector3(1f, 0.5f, 0.25f), title.Color);
        Assert.Equal(new System.Numerics.Vector3(0.1f, 0.2f, 0.3f), title.Glow);

        var clear = Assert.IsType<HonorificLayer>(reloaded[4]);
        Assert.True(clear.Clear);
        Assert.Null(clear.Color);

        var moodles = Assert.IsType<MoodlesLayer>(reloaded[5]);
        Assert.Single(moodles.Add);
        Assert.Single(moodles.AddPresets);
        Assert.Equal(2, moodles.Remove.Count);

        var equipment = Assert.IsType<EquipmentLayer>(reloaded[6]);
        Assert.Equal(3, equipment.Slots.Count);
        Assert.Equal(NothingItems.IdFor(EquipSlot.Feet), equipment.Slots[EquipSlot.Feet].ItemId);
        Assert.Null(equipment.Slots[EquipSlot.Body].ItemId);
        Assert.True(equipment.Slots[EquipSlot.Body].ApplyStains);
        Assert.Equal((byte)5, equipment.Slots[EquipSlot.Body].Stain1);
        Assert.Equal((byte)7, equipment.Slots[EquipSlot.Body].Stain2);
        Assert.True(equipment.Slots[EquipSlot.Body].Crest);
        Assert.Equal(10032ul, equipment.Slots[EquipSlot.Head].ItemId);
        Assert.Equal(BonusItems.IdFor(159), equipment.Bonus[BonusSlot.Glasses]);
        Assert.False(equipment.Meta[MetaIndex.HatVisible]);
        Assert.True(equipment.Meta[MetaIndex.WeaponVisible]);

        var appearance = Assert.IsType<AppearanceLayer>(reloaded[7]);
        Assert.Equal(60, appearance.Customize[CustomizeIndex.HairColor]);
        Assert.Equal(128, appearance.Customize[CustomizeIndex.Highlights]);
        Assert.Equal(8, appearance.Customize[CustomizeIndex.FacialFeature4]);
        Assert.True(appearance.Wetness);
        Assert.Equal(0.6f, appearance.Parameters["LipDiffuse"].Alpha);
        Assert.Equal(0.75f, appearance.Parameters["MuscleTone"].Scalar);
        Assert.Equal(new System.Numerics.Vector3(0.9f, 0.8f, 0.7f), appearance.Parameters["SkinDiffuse"].Color);

        // "Applies" semantics: an entry that only sets one group keeps state and priority inherited (null) after a round trip.
        var partial = Assert.IsType<PenumbraAdjustLayer>(reloaded[8]).Mods.Single();
        Assert.Null(partial.State);
        Assert.Null(partial.Priority);
        Assert.Equal(new[] { "Glossy" }, partial.Settings!["Skin"]);
        Assert.Contains("\"kind\": \"Equipment\"", json);
        Assert.Contains("\"kind\": \"Appearance\"", json);
    }

    [Fact]
    public void Enums_AreWrittenAsStrings_AndNamesAreCamelCase()
    {
        var json = JsonSettings.Serialize(Fixtures.FullProfile());

        Assert.Contains("\"op\": \"All\"", json);
        Assert.Contains("\"state\": \"Enable\"", json);
        Assert.Contains("\"kind\": \"LocalPlayer\"", json);
        Assert.Contains("\"kind\": \"Group\"", json);
        Assert.Contains("\"kind\": \"Glamourer\"", json);
        Assert.Contains("\"passGroups\"", json);
        Assert.Contains("\"equipment\": \"Head, Body\"", json);
        Assert.DoesNotContain("\"PassGroups\"", json);
        Assert.DoesNotContain("\"isEverything\"", json);
        Assert.DoesNotContain("\"isEmpty\"", json);
        Assert.DoesNotContain("\"Kind\"", json);
        // Dictionary keys (mod option groups) keep their casing.
        Assert.Contains("\"Body options\"", json);
    }

    [Fact]
    public void Vector3_IsWrittenAsXyz()
    {
        var json = JsonSettings.Serialize(new HonorificLayer { Title = "t", Color = new System.Numerics.Vector3(1, 2, 3) });
        var obj  = JObject.Parse(json);
        Assert.Equal(1f, obj["color"]!["x"]!.Value<float>());
        Assert.Equal(2f, obj["color"]!["y"]!.Value<float>());
        Assert.Equal(3f, obj["color"]!["z"]!.Value<float>());
    }

    [Fact]
    public void UnknownConditionAndLayerKinds_AreRetained_Verbatim()
    {
        const string json = """
            {
              "version": 1,
              "id": "6f1c0000-0000-0000-0000-000000000001",
              "name": "Future",
              "enabled": true,
              "targets": [ { "kind": "LocalPlayer" } ],
              "passGroups": [ {
                "id": "6f1c0000-0000-0000-0000-000000000002", "name": "G",
                "conditions": { "op": "All", "children": [
                  { "kind": "Teleport", "id": "6f1c0000-0000-0000-0000-000000000003", "aetherytes": [ 1, 2 ], "nested": { "a": true } },
                  { "kind": "Weather", "groups": [ "Wet" ] } ] },
                "passes": [ {
                  "id": "6f1c0000-0000-0000-0000-000000000004", "name": "P",
                  "layers": [
                    { "kind": "CustomizePlus", "id": "6f1c0000-0000-0000-0000-000000000005", "profileId": "abc", "scale": [ 1.0, 2.5 ] },
                    { "kind": "Glamourer", "designId": "6f1c0000-0000-0000-0000-000000000006" } ]
                } ]
              } ]
            }
            """;

        var profile = JsonSettings.Deserialize<Profile>(json);
        var group   = Assert.IsType<ConditionGroup>(profile.PassGroups[0].Conditions.Root);
        var unknownCondition = Assert.IsType<UnknownCondition>(group.Children[0]);
        Assert.Equal("Teleport", unknownCondition.Kind);
        Assert.False(unknownCondition.Matches(Lamina.Runtime.GameStateSnapshot.Empty));
        Assert.IsType<WeatherCondition>(group.Children[1]);

        var pass = profile.PassGroups[0].Passes[0];
        var unknownLayer = Assert.IsType<UnknownLayer>(pass.Layers[0]);
        Assert.Equal("CustomizePlus", unknownLayer.Kind);
        Assert.IsType<GlamourerLayer>(pass.Layers[1]);

        var written  = JObject.Parse(JsonSettings.Serialize(profile));
        var original = JObject.Parse(json);
        Assert.True(JToken.DeepEquals(original["passGroups"]![0]!["conditions"]!["children"]![0], written["passGroups"]![0]!["conditions"]!["children"]![0]));
        Assert.True(JToken.DeepEquals(original["passGroups"]![0]!["passes"]![0]!["layers"]![0], written["passGroups"]![0]!["passes"]![0]!["layers"]![0]));
    }

    [Fact]
    public void TddExampleProfile_Deserializes()
    {
        // TDD 10.6 with real ids in place of the "…" placeholders.
        const string json = """
            {
              "version": 1,
              "id": "6f1c0000-0000-0000-0000-00000000aaaa",
              "name": "Main",
              "enabled": true,
              "targets": [ { "kind": "LocalPlayer" } ],
              "passGroups": [ {
                "id": "6f1c0000-0000-0000-0000-00000000bbbb", "name": "Home",
                "isFinal": false, "priority": 0,
                "conditions": { "op": "All", "children": [
                  { "kind": "Housing", "where": "OwnHouse" } ] },
                "passes": [ {
                  "id": "6f1c0000-0000-0000-0000-00000000cccc", "name": "Rainy night",
                  "conditions": { "op": "All", "children": [
                    { "kind": "Weather", "groups": ["Wet"] },
                    { "kind": "Time", "clock": "ET", "from": "18:00", "to": "06:00" } ] },
                  "layers": [
                    { "kind": "Glamourer", "designId": "6f1c0000-0000-0000-0000-00000000dddd" },
                    { "kind": "Honorific", "title": "Rain Walker" }
                  ]
                } ]
              } ]
            }
            """;

        var profile = JsonSettings.Deserialize<Profile>(json);
        Assert.Equal("Main", profile.Name);
        Assert.True(profile.Enabled);
        Assert.Single(profile.Targets);
        Assert.Equal(TargetKind.LocalPlayer, profile.Targets[0].Kind);

        var home = Assert.Single(profile.PassGroups);
        Assert.Equal("Home", home.Name);
        Assert.False(home.IsFinal);
        var homeRoot = Assert.IsType<ConditionGroup>(home.Conditions.Root);
        var housing  = Assert.IsType<HousingCondition>(Assert.Single(homeRoot.Children));
        Assert.Equal(HousingWhere.OwnHouse, housing.Where);

        var pass = Assert.Single(home.Passes);
        var passRoot = Assert.IsType<ConditionGroup>(pass.Conditions.Root);
        Assert.Equal(GroupOp.All, passRoot.Op);
        var weather = Assert.IsType<WeatherCondition>(passRoot.Children[0]);
        Assert.Equal(new[] { WeatherGroup.Wet }, weather.Groups);
        var time = Assert.IsType<TimeCondition>(passRoot.Children[1]);
        Assert.Equal(ClockKind.ET, time.Clock);
        Assert.Equal(18 * 60, time.FromMinutes);
        Assert.Equal(6 * 60, time.ToMinutes);

        Assert.Equal(2, pass.Layers.Count);
        Assert.Equal(Guid.Parse("6f1c0000-0000-0000-0000-00000000dddd"), Assert.IsType<GlamourerLayer>(pass.Layers[0]).DesignId);
        Assert.Equal("Rain Walker", Assert.IsType<HonorificLayer>(pass.Layers[1]).Title);
    }

    [Fact]
    public void EmptyConditionStack_RoundTrips_AsEmpty()
    {
        var pass = new Pass { Name = "P" };
        var json = JsonSettings.Serialize(pass);
        var reloaded = JsonSettings.Deserialize<Pass>(json);
        Assert.True(reloaded.Conditions.IsEmpty);

        var withoutProperty = JsonSettings.Deserialize<Pass>("""{ "name": "X" }""");
        Assert.NotNull(withoutProperty.Conditions);
        Assert.True(withoutProperty.Conditions.IsEmpty);

        var withNull = JsonSettings.Deserialize<Pass>("""{ "name": "X", "conditions": null }""");
        Assert.NotNull(withNull.Conditions);
        Assert.True(withNull.Conditions.IsEmpty);
    }

    [Fact]
    public void DefaultTargets_AreReplaced_NotAppended()
    {
        var reloaded = JsonSettings.Deserialize<Profile>("""{ "name": "X", "targets": [ { "kind": "LocalPlayer" } ] }""");
        Assert.Single(reloaded.Targets);
    }
}
