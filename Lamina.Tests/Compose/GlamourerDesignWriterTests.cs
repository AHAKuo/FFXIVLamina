using Glamourer.Api.Enums;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Xunit;

namespace Lamina.Tests.Compose;

public class GlamourerDesignWriterTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");

    private static VirtualDesign Freeze(DesignDraft d)
        => d.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());

    /// <summary> A design that sets something in every section a Glamourer design can hold. </summary>
    private static DesignDraft FullDraft()
    {
        var d = new DesignDraft();
        d.Items[EquipSlot.Head]              = new Field<EquipItem>(new EquipItem(100), Src);
        d.Items[EquipSlot.MainHand]          = new Field<EquipItem>(new EquipItem(16054), Src);
        d.Stains[EquipSlot.Head]             = new Field<Stains>(new Stains(3, 4), Src);
        d.Stains[EquipSlot.Legs]             = new Field<Stains>(new Stains(9, 0), Src);
        d.Crests[EquipSlot.Body]             = new Field<bool>(true, Src);
        d.Crests[EquipSlot.OffHand]          = new Field<bool>(false, Src);
        d.Bonus[BonusSlot.Glasses]           = new Field<ulong>(844424946909184, Src);
        d.Customize[CustomizeIndex.Face]     = new Field<int>(2, Src);
        d.Customize[CustomizeIndex.Hairstyle] = new Field<int>(195, Src);
        d.ModelId                            = new Field<uint>(7, Src);
        d.Meta[MetaIndex.HatVisible]         = new Field<bool>(false, Src);
        d.Meta[MetaIndex.VisorToggled]       = new Field<bool>(true, Src);
        d.Meta[MetaIndex.WeaponVisible]      = new Field<bool>(false, Src);
        d.Meta[MetaIndex.VieraEarsVisible]   = new Field<bool>(true, Src);
        d.Meta[MetaIndex.Wetness]            = new Field<bool>(true, Src);
        d.Parameters["SkinDiffuse"]          = new Field<JObject>(new JObject { ["Red"] = 0.89, ["Green"] = 0.86, ["Blue"] = 0.84 }, Src);
        d.Parameters["MuscleTone"]           = new Field<JObject>(new JObject { ["Percentage"] = 1 }, Src);
        d.Materials["999"]                   = new Field<JToken>(new JObject { ["X"] = 1, ["Apply"] = true }, Src);
        d.AddMod(ModContribution.From(new ModState
        {
            ModDirectory = "Wildkini",
            ModName      = "Wildkini",
            State        = ModToggle.Enable,
            Priority     = 5,
            Settings     = new Dictionary<string, List<string>>
            {
                ["Texture Option"] = new() { "Vulnerable" },
                ["Extra"]          = new() { "See-Through", "Gay" },
            },
        }, Src));
        d.AddMod(ModContribution.From(new ModState { ModDirectory = "Winter Soldier", ModName = "Winter Soldier", State = ModToggle.Disable, Priority = null }, Src));
        return d;
    }

    [Fact]
    public void FileVersion3WithDesignMetadata()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var obj    = GlamourerDesignWriter.Build(VirtualDesign.Empty, "My design", "A description", includeMods: true);

        Assert.Equal(3, obj["FileVersion"]!.Value<int>());
        Assert.True(Guid.TryParse(obj["Identifier"]!.Value<string>(), out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.True(obj["CreationDate"]!.Value<long>() >= before);
        Assert.Equal(obj["CreationDate"]!.Value<long>(), obj["LastEdit"]!.Value<long>());
        Assert.Equal("My design", obj["Name"]!.Value<string>());
        Assert.Equal("A description", obj["Description"]!.Value<string>());
        Assert.Equal(string.Empty, obj["Color"]!.Value<string>());
        Assert.False(obj["QuickDesign"]!.Value<bool>());
        Assert.Empty((JArray)obj["Tags"]!);
        Assert.False(obj["WriteProtected"]!.Value<bool>());
    }

    [Fact]
    public void EmptyDesignYieldsEmptySections()
    {
        var obj = GlamourerDesignWriter.Build(VirtualDesign.Empty, "Empty", string.Empty, includeMods: true);

        foreach (var section in new[] { "Equipment", "Bonus", "Customize", "Parameters", "Materials" })
        {
            Assert.IsType<JObject>(obj[section]);
            Assert.Empty((JObject)obj[section]!);
        }
        Assert.IsType<JArray>(obj["Mods"]);
        Assert.Empty((JArray)obj["Mods"]!);

        var parsed = GlamourerDesignParser.Parse(obj, Provenance.None, includeMods: true);
        Assert.True(parsed.IsEmpty);
    }

    [Fact]
    public void NoModsArrayWithoutIncludeMods()
    {
        var obj = GlamourerDesignWriter.Build(Freeze(FullDraft()), "Design", string.Empty, includeMods: false);
        Assert.Null(obj["Mods"]);
    }

    [Fact]
    public void RoundTripsThroughTheParser()
    {
        var design = Freeze(FullDraft());
        var obj    = GlamourerDesignWriter.Build(design, "Round trip", string.Empty, includeMods: true);
        var parsed = GlamourerDesignParser.Parse(obj, Provenance.None, includeMods: true);

        Assert.Equal(design.Items.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Items.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        Assert.Equal(design.Stains.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Stains.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        Assert.Equal(design.Crests.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Crests.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        Assert.Equal(design.Bonus.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Bonus.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        Assert.Equal(design.Customize.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Customize.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        Assert.Equal(design.ModelId!.Value.Value, parsed.ModelId!.Value.Value);
        Assert.Equal(design.Meta.ToDictionary(kv => kv.Key, kv => kv.Value.Value), parsed.Meta.ToDictionary(kv => kv.Key, kv => kv.Value.Value));

        Assert.Equal(design.Parameters.Keys.OrderBy(k => k), parsed.Parameters.Keys.OrderBy(k => k));
        foreach (var (key, field) in design.Parameters)
            Assert.True(JToken.DeepEquals(field.Value, parsed.Parameters[key].Value), key);
        Assert.Equal(design.Materials.Keys, parsed.Materials.Keys);
        foreach (var (key, field) in design.Materials)
            Assert.True(JToken.DeepEquals(field.Value, parsed.Materials[key].Value), key);

        Assert.Equal(design.Mods.Keys.OrderBy(k => k), parsed.Mods.Keys.OrderBy(k => k));
        foreach (var (dir, mod) in design.Mods)
        {
            var back = parsed.Mods[dir];
            Assert.Equal(mod.ModName, back.ModName);
            Assert.Equal(mod.State!.Value.Value, back.State!.Value.Value);
            Assert.Equal(mod.Priority?.Value ?? 0, back.Priority!.Value.Value);
            Assert.Equal(mod.Settings.Keys.OrderBy(k => k), back.Settings.Keys.OrderBy(k => k));
            foreach (var (group, options) in mod.Settings)
                Assert.Equal(options.Value.OrderBy(o => o), back.Settings[group].Value.OrderBy(o => o));
        }
    }

    [Fact]
    public void EquipmentFlagsOnlyForSetParts()
    {
        var obj       = GlamourerDesignWriter.Build(Freeze(FullDraft()), "Flags", string.Empty, includeMods: false);
        var equipment = (JObject)obj["Equipment"]!;

        // Head: item + stains, no crest set.
        Assert.True(equipment["Head"]!["Apply"]!.Value<bool>());
        Assert.True(equipment["Head"]!["ApplyStain"]!.Value<bool>());
        Assert.False(equipment["Head"]!["ApplyCrest"]!.Value<bool>());
        Assert.Equal(3, equipment["Head"]!["Stain"]!.Value<int>());
        Assert.Equal(4, equipment["Head"]!["Stain2"]!.Value<int>());

        // Legs: stains only.
        Assert.Null(equipment["Legs"]!["ItemId"]);
        Assert.False(equipment["Legs"]!["Apply"]!.Value<bool>());
        Assert.True(equipment["Legs"]!["ApplyStain"]!.Value<bool>());
        Assert.Null(equipment["Legs"]!["ApplyCrest"]);

        // Body: crest only.
        Assert.False(equipment["Body"]!["Apply"]!.Value<bool>());
        Assert.True(equipment["Body"]!["ApplyCrest"]!.Value<bool>());
        Assert.True(equipment["Body"]!["Crest"]!.Value<bool>());
        Assert.False(equipment["OffHand"]!["Crest"]!.Value<bool>());
        Assert.True(equipment["OffHand"]!["ApplyCrest"]!.Value<bool>());

        Assert.Null(equipment["Feet"]);
        Assert.False(equipment["Hat"]!["Show"]!.Value<bool>());
        Assert.True(equipment["Hat"]!["Apply"]!.Value<bool>());
        Assert.True(equipment["Visor"]!["IsToggled"]!.Value<bool>());
        Assert.True(equipment["VieraEars"]!["Show"]!.Value<bool>());

        var customize = (JObject)obj["Customize"]!;
        Assert.True(customize["Face"]!["Apply"]!.Value<bool>());
        Assert.Null(customize["Race"]);
        Assert.True(customize["Wetness"]!["Value"]!.Value<bool>());
        Assert.True(customize["Wetness"]!["Apply"]!.Value<bool>());
        Assert.Equal(7u, customize["ModelId"]!.Value<uint>());

        Assert.True(obj["Parameters"]!["SkinDiffuse"]!["Apply"]!.Value<bool>());
        Assert.Equal(1, obj["Materials"]!["999"]!["X"]!.Value<int>());
    }

    [Fact]
    public void ModsArrayMatchesGlamourersShape()
    {
        var fixture      = Build.Fixture("design_full.json");
        var fixtureEntry = (JObject)((JArray)fixture["Mods"]!)[0];
        var fixtureGroup = (JObject)((JArray)fixtureEntry["Settings"]!)[0];
        var fixtureOpt   = (JObject)((JArray)fixtureGroup["Options"]!)[0];

        var obj   = GlamourerDesignWriter.Build(Freeze(FullDraft()), "Mods", string.Empty, includeMods: true);
        var mods  = (JArray)obj["Mods"]!;
        Assert.Equal(2, mods.Count);

        var entry = mods.OfType<JObject>().Single(m => m["Directory"]!.Value<string>() == "Wildkini");
        Assert.Equal(fixtureEntry.Properties().Select(p => p.Name).OrderBy(n => n), entry.Properties().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("Wildkini", entry["Name"]!.Value<string>());
        Assert.Equal(1, entry["Version"]!.Value<int>());
        Assert.Equal("Enabled", entry["State"]!.Value<string>());
        Assert.Equal(5, entry["Priority"]!.Value<int>());

        var groups = ((JArray)entry["Settings"]!).OfType<JObject>().ToList();
        Assert.Equal(new[] { "Extra", "Texture Option" }, groups.Select(g => g["Name"]!.Value<string>()));
        var fixtureGroupKeys = fixtureGroup.Properties().Select(p => p.Name).ToHashSet();
        var fixtureOptKeys   = fixtureOpt.Properties().Select(p => p.Name).ToHashSet();
        foreach (var group in groups)
        {
            // Glamourer also writes its own group / option Identifier; everything we write is a key Glamourer writes too.
            Assert.All(group.Properties(), p => Assert.Contains(p.Name, fixtureGroupKeys));
            foreach (var opt in ((JArray)group["Options"]!).OfType<JObject>())
            {
                Assert.All(opt.Properties(), p => Assert.Contains(p.Name, fixtureOptKeys));
                Assert.Equal("Enabled", opt["State"]!.Value<string>());
            }
        }
        Assert.Equal(new[] { "See-Through", "Gay" }, ((JArray)groups[0]["Options"]!).Select(o => o["Name"]!.Value<string>()));

        // Disabled mod, unset priority → 0, no settings → empty array.
        var disabled = mods.OfType<JObject>().Single(m => m["Directory"]!.Value<string>() == "Winter Soldier");
        Assert.Equal("Disabled", disabled["State"]!.Value<string>());
        Assert.Equal(0, disabled["Priority"]!.Value<int>());
        Assert.Empty((JArray)disabled["Settings"]!);
    }

    [Fact]
    public void AvailableSettingsListEveryOptionWithItsState()
    {
        var d = new DesignDraft();
        d.AddMod(ModContribution.From(new ModState
        {
            ModDirectory = "Wildkini",
            State        = ModToggle.Enable,
            Settings     = new Dictionary<string, List<string>> { ["Texture Option"] = new() { "Vulnerable", "Custom" } },
        }, Src));

        var available = new Dictionary<string, (string[] Options, GroupType Type)>
        {
            ["Texture Option"] = (new[] { "Plain", "Striped", "Vulnerable" }, GroupType.Single),
            ["Unset Group"]    = (new[] { "A", "B" }, GroupType.Multi),
        };
        var result = GlamourerDesignWriter.BuildWithReport(Freeze(d), "Options", string.Empty, includeMods: true, _ => available);
        var entry  = (JObject)((JArray)result.Design["Mods"]!)[0];
        var groups = ((JArray)entry["Settings"]!).OfType<JObject>().ToList();

        // Only the set group is listed; the lookup fills in the options the contribution does not name as Disabled.
        var group = Assert.Single(groups);
        Assert.Equal("Texture Option", group["Name"]!.Value<string>());
        var options = ((JArray)group["Options"]!).OfType<JObject>().ToDictionary(o => o["Name"]!.Value<string>()!, o => o["State"]!.Value<string>());
        Assert.Equal(new Dictionary<string, string?>
        {
            ["Plain"]      = "Disabled",
            ["Striped"]    = "Disabled",
            ["Vulnerable"] = "Enabled",
            ["Custom"]     = "Enabled",
        }, options);
        Assert.Equal("Wildkini", entry["Name"]!.Value<string>());

        var parsed = GlamourerDesignParser.Parse(result.Design, Provenance.None, includeMods: true);
        Assert.Equal(new[] { "Vulnerable", "Custom" }, parsed.Mods["Wildkini"].Settings["Texture Option"].Value);
    }

    [Fact]
    public void ModsWithInheritedStateAreSkippedAndReported()
    {
        var d = new DesignDraft();
        d.AddMod(ModContribution.From(new ModState { ModDirectory = "Inherit", State = null, Priority = 3 }, Src));
        d.AddMod(ModContribution.From(new ModState { ModDirectory = "Kept", State = ModToggle.Enable }, Src));

        var result = GlamourerDesignWriter.BuildWithReport(Freeze(d), "Skip", string.Empty, includeMods: true, null);

        Assert.Equal(new[] { "Inherit" }, result.SkippedMods);
        var mods = (JArray)result.Design["Mods"]!;
        Assert.Equal(new[] { "Kept" }, mods.Select(m => m["Directory"]!.Value<string>()));
    }

    [Fact]
    public void FakeBridgeRecordsAddDesign()
    {
        var bridge = new Lamina.Tests.Fakes.FakeGlamourerBridge();
        var obj    = GlamourerDesignWriter.Build(Freeze(FullDraft()), "Recorded", string.Empty, includeMods: true);

        var (code, id) = bridge.AddDesign(obj, "Recorded");

        Assert.Equal(GlamourerApiEc.Success, code);
        Assert.NotEqual(Guid.Empty, id);
        var call = Assert.Single(bridge.CallsNamed("AddDesign"));
        Assert.Equal("Recorded", call.Args[1]);
        Assert.True(JToken.DeepEquals(obj, bridge.AddedDesigns[id].Design));
        Assert.Equal("Recorded", bridge.GetDesigns()[id].DisplayName);
        Assert.True(JToken.DeepEquals(obj, bridge.GetDesign(id)));

        bridge.Available = false;
        Assert.Equal((GlamourerApiEc.UnknownError, Guid.Empty), bridge.AddDesign(obj, "Offline"));
    }

    [Fact]
    public void ComposeFakeBridgeListsAddedDesign()
    {
        var bridge = new FakeGlamourerBridge();
        var obj    = GlamourerDesignWriter.Build(Freeze(FullDraft()), "Listed", string.Empty, includeMods: false);

        var (code, id) = bridge.AddDesign(obj, "Listed");

        Assert.Equal(GlamourerApiEc.Success, code);
        Assert.Equal("Listed", bridge.GetDesigns()[id].DisplayName);
        Assert.True(JToken.DeepEquals(obj, bridge.GetDesign(id)));
    }
}
