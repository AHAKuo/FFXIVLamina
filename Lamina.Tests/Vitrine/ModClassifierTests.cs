using Vitrine;
using Vitrine.Catalog;
using Xunit;

namespace Lamina.Tests.Vitrine;

public class ModClassifierTests
{
    [Theory]
    [InlineData("Miqo'te Keeper of the Moon Female Hair 108", VitrineCategory.Hair)]
    [InlineData("Hyur Midlander Female Hairstyle 5", VitrineCategory.Hair)]
    [InlineData("Au Ra Raen Female Face 3", VitrineCategory.Face)]
    [InlineData("Hrothgar Male Face Paint 12", VitrineCategory.Face)]
    [InlineData("Viera Female Decal 4", VitrineCategory.Face)]
    [InlineData("Au Ra Xaela Female Tail 2", VitrineCategory.Tail)]
    [InlineData("Viera Male Ears 1", VitrineCategory.Ears)]
    [InlineData("Hyur Highlander Female Body", VitrineCategory.Body)]
    [InlineData("Lalafell Plainsfolk Female Skin", VitrineCategory.Skin)]
    [InlineData("Emote: Sit", VitrineCategory.Emote)]
    [InlineData("/dance", VitrineCategory.Emote)]
    [InlineData("Action: Fast Blade", VitrineCategory.Animation)]
    [InlineData("Idle 1", VitrineCategory.Animation)]
    [InlineData("vfx/common/eff/dk05th_stup0t.avfx", VitrineCategory.Vfx)]
    [InlineData("sound/battle/se_bt_fire.scd", VitrineCategory.Sfx)]
    [InlineData("Footsteps Sound", VitrineCategory.Sfx)]
    [InlineData("ui/uld/CharaMake.tex", VitrineCategory.Ui)]
    [InlineData("Job Icon Set", VitrineCategory.Ui)]
    [InlineData("Minion: Wind-up Cirina", VitrineCategory.Minion)]
    [InlineData("Mount: Company Chocobo", VitrineCategory.Mount)]
    [InlineData("bgcommon/hou/indoor/general/0001/texture.tex", VitrineCategory.Housing)]
    [InlineData("Oasis Lounge Chair Furnishing", VitrineCategory.Housing)]
    [InlineData("Monster: Behemoth", VitrineCategory.Npc)]
    [InlineData("Thavnairian Bustier", VitrineCategory.Gear)]
    [InlineData("Scion Thaumaturge's Robe", VitrineCategory.Gear)]
    [InlineData("Eastern Lady's Loincloth", VitrineCategory.Gear)]
    [InlineData("Tidal Wave Earrings", VitrineCategory.Accessory)]
    [InlineData("Diamond Necklace", VitrineCategory.Accessory)]
    [InlineData("Curtana", VitrineCategory.Other)]
    [InlineData("Dark Knight Greatsword", VitrineCategory.Weapon)]
    [InlineData("chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl", VitrineCategory.Weapon)]
    [InlineData("chara/human/c0201/obj/hair/h0005/model/c0201h0005_hir.mdl", VitrineCategory.Hair)]
    [InlineData("chara/equipment/e0001/model/c0101e0001_top.mdl", VitrineCategory.Gear)]
    [InlineData("", VitrineCategory.Unknown)]
    public void ClassifiesByName(string name, VitrineCategory expected)
        => Assert.Equal(expected, ModClassifier.Classify(name));

    private sealed class FakeEmote { public uint RowId => 1; }
    private sealed class FakeAction { public uint RowId => 2; }
    private sealed class FakeItem { public uint RowId => 3; public string EquipSlotCategory => "Ears"; }
    private sealed class FakeItemBody { public uint RowId => 4; public string EquipSlotCategory => "Body"; }
    private sealed class FakeCompanion { public uint RowId => 5; }

    [Fact]
    public void ValueTypeWinsOverPlainName()
    {
        Assert.Equal(VitrineCategory.Emote, ModClassifier.Classify("Sit", new FakeEmote()));
        Assert.Equal(VitrineCategory.Animation, ModClassifier.Classify("Fast Blade", new FakeAction()));
        Assert.Equal(VitrineCategory.Accessory, ModClassifier.Classify("Some Thing", new FakeItem()));
        Assert.Equal(VitrineCategory.Gear, ModClassifier.Classify("Some Thing", new FakeItemBody()));
        Assert.Equal(VitrineCategory.Minion, ModClassifier.Classify("Cirina", new FakeCompanion()));
    }

    [Fact]
    public void ChangedItemTypeTupleIsHonored()
    {
        Assert.Equal(VitrineCategory.Gear, ModClassifier.Classify("x", (global::Penumbra.Api.Enums.ChangedItemType.Item, 12u)));
        Assert.Equal(VitrineCategory.Emote, ModClassifier.Classify("x", (global::Penumbra.Api.Enums.ChangedItemType.Emote, 1u)));
        Assert.Equal(VitrineCategory.Hair, ModClassifier.Classify("Hyur Midlander Female Hair 1", (global::Penumbra.Api.Enums.ChangedItemType.Customization, 1u)));
    }

    [Fact]
    public void ModCategoryIsTheMostCommonAndIgnoresOther()
    {
        var items = new List<AffectedItem>
        {
            new(VitrineCategory.Other, "a", null),
            new(VitrineCategory.Other, "b", null),
            new(VitrineCategory.Other, "c", null),
            new(VitrineCategory.Hair, "d", null),
        };
        Assert.Equal(VitrineCategory.Hair, ModClassifier.ClassifyMod(items));
        Assert.Equal(VitrineCategory.Other, ModClassifier.ClassifyMod(Array.Empty<AffectedItem>()));
        Assert.Equal(VitrineCategory.Gear, ModClassifier.ClassifyMod(new[]
        {
            new AffectedItem(VitrineCategory.Gear, "a", null),
            new AffectedItem(VitrineCategory.Gear, "b", null),
            new AffectedItem(VitrineCategory.Accessory, "c", null),
        }));
    }
}
