using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Compose;

public class GlamourerStateBuilderTests
{
    private static readonly Provenance Src = new(null, null, null, "test");

    private static VirtualDesign Freeze(DesignDraft d)
        => d.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());

    [Fact]
    public void EmptyDesignHasAllSectionsAndNoApplyFlags()
    {
        var state = GlamourerStateBuilder.Build(VirtualDesign.Empty);

        Assert.Equal(1, state["FileVersion"]!.Value<int>());
        foreach (var section in new[] { "Equipment", "Bonus", "Customize", "Parameters", "Materials" })
            Assert.IsType<JObject>(state[section]);
        Assert.Null(state["Mods"]);
        Assert.Empty((JObject)state["Equipment"]!);
    }

    [Fact]
    public void ApplyFlagsOnlyForSetFields()
    {
        var d = new DesignDraft();
        d.Items[EquipSlot.Head]            = new Field<EquipItem>(new EquipItem(100), Src);
        d.Stains[EquipSlot.Body]           = new Field<Stains>(new Stains(3, 4), Src);
        d.Crests[EquipSlot.OffHand]        = new Field<bool>(true, Src);
        d.Customize[CustomizeIndex.Face]   = new Field<int>(2, Src);
        d.Meta[MetaIndex.HatVisible]       = new Field<bool>(false, Src);
        d.Meta[MetaIndex.Wetness]          = new Field<bool>(true, Src);
        d.Bonus[BonusSlot.Glasses]         = new Field<ulong>(42, Src);
        d.ModelId                          = new Field<uint>(7, Src);
        d.Parameters["SkinDiffuse"]        = new Field<JObject>(new JObject { ["Red"] = 1.0, ["Green"] = 0.5, ["Blue"] = 0.0 }, Src);
        d.Materials["999"]                 = new Field<JToken>(new JObject { ["X"] = 1 }, Src);
        d.AddMod(ModContribution.From(new ModState { ModDirectory = "SomeMod" }, Src));

        var s = GlamourerStateBuilder.Build(Freeze(d));
        var equipment = (JObject)s["Equipment"]!;

        Assert.True(equipment["Head"]!["Apply"]!.Value<bool>());
        Assert.False(equipment["Head"]!["ApplyStain"]!.Value<bool>());
        Assert.True(equipment["Head"]!["ApplyCrest"]!.Value<bool>() == false);
        Assert.Equal(100ul, equipment["Head"]!["ItemId"]!.Value<ulong>());

        Assert.False(equipment["Body"]!["Apply"]!.Value<bool>());
        Assert.True(equipment["Body"]!["ApplyStain"]!.Value<bool>());
        Assert.Null(equipment["Body"]!["ItemId"]);
        Assert.Equal(3, equipment["Body"]!["Stain"]!.Value<int>());
        Assert.Equal(4, equipment["Body"]!["Stain2"]!.Value<int>());

        Assert.True(equipment["OffHand"]!["ApplyCrest"]!.Value<bool>());
        Assert.True(equipment["OffHand"]!["Crest"]!.Value<bool>());
        Assert.Null(equipment["Legs"]);

        Assert.False(equipment["Hat"]!["Show"]!.Value<bool>());
        Assert.True(equipment["Hat"]!["Apply"]!.Value<bool>());
        Assert.Null(equipment["Visor"]);

        var customize = (JObject)s["Customize"]!;
        Assert.Equal(2, customize["Face"]!["Value"]!.Value<int>());
        Assert.True(customize["Face"]!["Apply"]!.Value<bool>());
        Assert.Null(customize["Hairstyle"]);
        Assert.True(customize["Wetness"]!["Value"]!.Value<bool>());
        Assert.Equal(7u, customize["ModelId"]!.Value<uint>());

        Assert.Equal(42ul, s["Bonus"]!["Glasses"]!["BonusId"]!.Value<ulong>());
        Assert.True(s["Parameters"]!["SkinDiffuse"]!["Apply"]!.Value<bool>());
        Assert.Equal(0.5, s["Parameters"]!["SkinDiffuse"]!["Green"]!.Value<double>());
        Assert.Equal(1, s["Materials"]!["999"]!["X"]!.Value<int>());
        Assert.Null(s["Mods"]);
    }

    [Fact]
    public void ParseOfBuildRoundTrips()
    {
        var original = GlamourerDesignParser.Parse(Build.Fixture("design_full.json"), Src, includeMods: false);
        original.Parameters["LipDiffuse"] = new Field<JObject>(new JObject { ["Red"] = 0.1, ["Green"] = 0.2, ["Blue"] = 0.3, ["Alpha"] = 0.4 }, Src);
        original.Meta[MetaIndex.Wetness]  = new Field<bool>(true, Src);
        original.Crests[EquipSlot.Head]   = new Field<bool>(true, Src);
        var frozen = Freeze(original);

        var rebuilt = GlamourerDesignParser.Parse(GlamourerStateBuilder.Build(frozen), Src, includeMods: true);
        var frozen2 = Freeze(rebuilt);

        Assert.Equal(frozen.ContentHash, frozen2.ContentHash);
        Assert.Equal(original.Items.Count, rebuilt.Items.Count);
        Assert.Equal(original.Stains.Count, rebuilt.Stains.Count);
        Assert.Equal(original.Meta.Count, rebuilt.Meta.Count);
        Assert.Equal(original.Customize.Count, rebuilt.Customize.Count);
        Assert.Empty(rebuilt.Mods);
    }

    [Fact]
    public void Build_CopiesIdentityFromBaseline_WithoutApply()
    {
        var draft = new DesignDraft();
        draft.Customize[CustomizeIndex.Hairstyle] = new Field<int>(5, Provenance.None);
        var design   = draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        var baseline = new JObject
        {
            ["Customize"] = new JObject
            {
                ["ModelId"] = 0,
                ["Race"]    = new JObject { ["Value"] = 4, ["Apply"] = true },
                ["Clan"]    = new JObject { ["Value"] = 7, ["Apply"] = true },
                ["Gender"]  = new JObject { ["Value"] = 1, ["Apply"] = true },
                ["BodyType"] = new JObject { ["Value"] = 1, ["Apply"] = true },
                ["Face"]    = new JObject { ["Value"] = 2, ["Apply"] = true },
            },
        };

        var state = GlamourerStateBuilder.Build(design, baseline);
        var customize = (JObject)state["Customize"]!;
        Assert.Equal(4, customize["Race"]!["Value"]!.Value<int>());
        Assert.False(customize["Race"]!["Apply"]!.Value<bool>());
        Assert.Equal(7, customize["Clan"]!["Value"]!.Value<int>());
        Assert.Equal(1, customize["Gender"]!["Value"]!.Value<int>());
        Assert.True(customize["Hairstyle"]!["Apply"]!.Value<bool>());
        Assert.Null(customize["Face"]);
    }

    [Fact]
    public void Build_AppliedClan_RewritesBaselineRaceToMatch()
    {
        var draft = new DesignDraft();
        draft.Customize[CustomizeIndex.Clan] = new Field<int>(7, Provenance.None); // Seeker of the Sun
        var design   = draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        var baseline = new JObject
        {
            ["Customize"] = new JObject
            {
                ["Race"] = new JObject { ["Value"] = 6 }, // Au Ra
                ["Clan"] = new JObject { ["Value"] = 11 },
            },
        };

        var customize = (JObject)GlamourerStateBuilder.Build(design, baseline)["Customize"]!;
        Assert.Equal(4, customize["Race"]!["Value"]!.Value<int>());
        Assert.True(customize["Race"]!["Apply"]!.Value<bool>());
        Assert.Equal(7, customize["Clan"]!["Value"]!.Value<int>());
    }

    [Fact]
    public void Build_AppliedRaceOnly_PicksAMatchingClan()
    {
        var draft = new DesignDraft();
        draft.Customize[CustomizeIndex.Race] = new Field<int>(8, Provenance.None); // Viera
        var design   = draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        var baseline = new JObject { ["Customize"] = new JObject { ["Clan"] = new JObject { ["Value"] = 7 } } };

        var customize = (JObject)GlamourerStateBuilder.Build(design, baseline)["Customize"]!;
        Assert.Equal(15, customize["Clan"]!["Value"]!.Value<int>());
        Assert.True(customize["Clan"]!["Apply"]!.Value<bool>());
    }
}
