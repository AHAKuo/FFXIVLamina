using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Compose;

public class GlamourerDesignParserTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");

    [Fact]
    public void OnlyAppliedFieldsContribute()
    {
        var design = Build.Design(d =>
        {
            d["Equipment"]!["Head"]       = Build.Slot(100, apply: true);
            d["Equipment"]!["Body"]       = Build.Slot(200, apply: false);
            d["Equipment"]!["Legs"]       = new JObject { ["ItemId"] = 300, ["Stain"] = 5 };   // no flags at all
            d["Equipment"]!["Hands"]      = Build.Slot(400, stain: 7, stain2: 8, apply: false, applyStain: true);
            d["Customize"]!["Hairstyle"]  = Build.Cust(12, apply: true);
            d["Customize"]!["HairColor"]  = Build.Cust(60, apply: false);
            d["Customize"]!["Race"]       = new JObject { ["Value"] = 4 };
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: true);

        Assert.Equal(new[] { EquipSlot.Head }, draft.Items.Keys);
        Assert.Equal(100ul, draft.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(new[] { EquipSlot.Hands }, draft.Stains.Keys);
        Assert.Equal(new Stains(7, 8), draft.Stains[EquipSlot.Hands].Value);
        Assert.Equal(new[] { CustomizeIndex.Hairstyle }, draft.Customize.Keys);
        Assert.Equal(12, draft.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.Equal(Src, draft.Items[EquipSlot.Head].Source);
    }

    [Fact]
    public void ApplyFlagsDefaultTrue_TreatsAbsentFlagsAsSet()
    {
        var design = Build.Design(d =>
        {
            d["Equipment"]!["Legs"]  = new JObject { ["ItemId"] = 300, ["Stain"] = 5, ["Stain2"] = 0 };
            d["Customize"]!["Race"]  = new JObject { ["Value"] = 4 };
            d["Customize"]!["ModelId"] = 0;
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: false, applyFlagsDefault: true);

        Assert.Equal(300ul, draft.Items[EquipSlot.Legs].Value.ItemId);
        Assert.Equal(new Stains(5, 0), draft.Stains[EquipSlot.Legs].Value);
        Assert.Equal(4, draft.Customize[CustomizeIndex.Race].Value);
        Assert.NotNull(draft.ModelId);
    }

    [Fact]
    public void MetaAndBonusAndWetness()
    {
        var design = Build.Design(d =>
        {
            d["Equipment"]!["Hat"]       = new JObject { ["Show"] = false, ["Apply"] = true };
            d["Equipment"]!["Visor"]     = new JObject { ["IsToggled"] = true, ["Apply"] = true };
            d["Equipment"]!["Weapon"]    = new JObject { ["Show"] = false };                     // not applied
            d["Equipment"]!["VieraEars"] = new JObject { ["Show"] = true, ["Apply"] = true };
            d["Customize"]!["Wetness"]   = new JObject { ["Value"] = true, ["Apply"] = true };
            d["Bonus"]!["Glasses"]       = new JObject { ["BonusId"] = 844424946909184, ["Apply"] = true };
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: false);

        Assert.False(draft.Meta[MetaIndex.HatVisible].Value);
        Assert.True(draft.Meta[MetaIndex.VisorToggled].Value);
        Assert.False(draft.Meta.ContainsKey(MetaIndex.WeaponVisible));
        Assert.True(draft.Meta[MetaIndex.VieraEarsVisible].Value);
        Assert.True(draft.Meta[MetaIndex.Wetness].Value);
        Assert.Equal(844424946909184ul, draft.Bonus[BonusSlot.Glasses].Value);
    }

    [Fact]
    public void CrestsOnlyOnCrestSlots()
    {
        var design = Build.Design(d =>
        {
            d["Equipment"]!["Body"]  = new JObject { ["ItemId"] = 1, ["Crest"] = true, ["ApplyCrest"] = true };
            d["Equipment"]!["Head"]  = new JObject { ["ItemId"] = 1, ["ApplyCrest"] = true };          // crest value absent = off
            d["Equipment"]!["Hands"] = new JObject { ["ItemId"] = 1, ["Crest"] = true, ["ApplyCrest"] = true };
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: false);

        Assert.True(draft.Crests[EquipSlot.Body].Value);
        Assert.False(draft.Crests[EquipSlot.Head].Value);
        Assert.False(draft.Crests.ContainsKey(EquipSlot.Hands));
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void ParametersStripApplyAndMaterialsAreOpaque()
    {
        var design = Build.Design(d =>
        {
            d["Parameters"]!["SkinDiffuse"] = new JObject { ["Red"] = 0.5, ["Green"] = 0.25, ["Blue"] = 1.0, ["Apply"] = true };
            d["Parameters"]!["MuscleTone"]  = new JObject { ["Percentage"] = 1.0 };
            d["Materials"] = new JObject { ["12345"] = new JObject { ["DiffuseR"] = 1.0, ["Enabled"] = true } };
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: false);

        Assert.Single(draft.Parameters);
        Assert.Null(draft.Parameters["SkinDiffuse"].Value["Apply"]);
        Assert.Equal(0.25, draft.Parameters["SkinDiffuse"].Value["Green"]!.Value<double>());
        Assert.Single(draft.Materials);
        Assert.True(JToken.DeepEquals(draft.Materials["12345"].Value, design["Materials"]!["12345"]));
    }

    [Fact]
    public void ModsParsedToModState_AndSkippedWhenNotIncluded()
    {
        var design = Build.Fixture("design_full.json");

        var withMods = GlamourerDesignParser.Parse(design, Src, includeMods: true);
        var noMods   = GlamourerDesignParser.Parse(design, Src, includeMods: false);

        Assert.Empty(noMods.Mods);
        Assert.Equal(2, withMods.Mods.Count);

        var wildkini = withMods.Mods["Wildkini"].ToModState();
        Assert.Equal(ModToggle.Enable, wildkini.State);
        Assert.Equal(0, wildkini.Priority);
        Assert.Equal("Wildkini", wildkini.ModName);
        Assert.NotNull(wildkini.Settings);
        Assert.Equal(new[] { "Milky Eve Chest" }, wildkini.Settings!["Wildkini"]);
        Assert.Equal(new[] { "Vulnerable" }, wildkini.Settings["Texture Option"]);
        Assert.Equal(new[] { "See-Through", "Gay" }, wildkini.Settings["Extra"]);

        // Case-insensitive mod keys.
        Assert.True(withMods.Mods.ContainsKey("wildkini"));
    }

    [Fact]
    public void ModsHonorDisabledStateAndRemove()
    {
        var design = Build.Design(d => d["Mods"] = new JArray
        {
            new JObject { ["Name"] = "A", ["Directory"] = "A", ["State"] = "Disabled", ["Priority"] = 3 },
            new JObject { ["Name"] = "B", ["Directory"] = "B", ["State"] = "Enabled", ["Remove"] = true },
            new JObject { ["Name"] = "C", ["Directory"] = "C", ["State"] = "Enabled" },
            new JObject { ["Name"] = "no dir" },
        });

        var draft = GlamourerDesignParser.Parse(design, Src, includeMods: true);

        Assert.Equal(3, draft.Mods.Count);
        Assert.Equal(ModToggle.Disable, draft.Mods["A"].State!.Value.Value);
        Assert.Equal(3, draft.Mods["A"].Priority!.Value.Value);
        Assert.Equal(ModToggle.Disable, draft.Mods["B"].State!.Value.Value);
        Assert.Equal(ModToggle.Enable, draft.Mods["C"].State!.Value.Value);
        Assert.Empty(draft.Mods["C"].Settings);
    }

    [Fact]
    public void RealFixtureParsesEveryAppliedSlot()
    {
        var design = Build.Fixture("design_full.json");
        var draft  = GlamourerDesignParser.Parse(design, Src, includeMods: true);

        Assert.Equal(12, draft.Items.Count);
        Assert.Equal(12, draft.Stains.Count);
        Assert.Equal(3, draft.Crests.Count);
        Assert.Equal(4, draft.Meta.Count);           // Hat, VieraEars, Visor, Weapon applied; Wetness not
        Assert.Single(draft.Customize);              // BodyType
        Assert.Equal(CustomizeIndex.BodyType, draft.Customize.Keys.Single());
        Assert.Single(draft.Bonus);
        Assert.Empty(draft.Parameters);
        Assert.Equal(2, draft.Mods.Count);
    }

    [Fact]
    public void ParseModAssociations_Standalone()
    {
        var mods = GlamourerDesignParser.ParseModAssociations(Build.Fixture("design_full.json"));
        Assert.Equal(2, mods.Count);
        Assert.Contains(mods, m => m.ModDirectory == "Winter Soldier");
    }
}
