using System.Numerics;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Runtime.State;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> Equipment Changes and Appearance Changes layers: resolution, cascade order and Macro Override masks. </summary>
public class NativeLayerTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
    private readonly PassCompositor _compositor = new();
    private readonly InstantEvaluator _eval = new();

    private static LayerResolver Resolver()
        => Build.Resolver(new FakeGlamourerBridge());

    // ---- Equipment ---------------------------------------------------------------------------------

    [Fact]
    public void Equipment_ItemDyesCrestAndVisibilityLandInTheDraft()
    {
        var layer = new EquipmentLayer
        {
            Slots =
            {
                [EquipSlot.Head]  = new SlotEdit { ItemId = 123, ApplyStains = true, Stain1 = 4, Stain2 = 9, Crest = true },
                [EquipSlot.Feet]  = new SlotEdit { ItemId = NothingItems.IdFor(EquipSlot.Feet) },
                [EquipSlot.Body]  = new SlotEdit { ApplyStains = true, Stain1 = 2 },
                [EquipSlot.Hands] = new SlotEdit { Crest = true },                 // hands carry no crest: ignored
                [EquipSlot.Legs]  = new SlotEdit(),                                // nothing set
            },
            Meta = { [MetaIndex.HatVisible] = false, [MetaIndex.Wetness] = true },  // wetness is not an equipment toggle
        };

        var draft = Resolver().Resolve(layer, Src, out var reason);

        Assert.Null(reason);
        Assert.NotNull(draft);
        Assert.Equal(123ul, draft!.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(new Stains(4, 9), draft.Stains[EquipSlot.Head].Value);
        Assert.True(draft.Crests[EquipSlot.Head].Value);
        Assert.Equal(NothingItems.IdFor(EquipSlot.Feet), draft.Items[EquipSlot.Feet].Value.ItemId);
        Assert.False(draft.Items.ContainsKey(EquipSlot.Body));
        Assert.Equal(new Stains(2, 0), draft.Stains[EquipSlot.Body].Value);
        Assert.Empty(draft.Crests.Where(kv => kv.Key == EquipSlot.Hands));
        Assert.False(draft.Items.ContainsKey(EquipSlot.Legs));
        Assert.False(draft.Meta[MetaIndex.HatVisible].Value);
        Assert.False(draft.Meta.ContainsKey(MetaIndex.Wetness));
        Assert.Equal(Src, draft.Items[EquipSlot.Head].Source);
    }

    [Fact]
    public void Equipment_WithNothingSetIsSkipped()
    {
        var draft = Resolver().Resolve(new EquipmentLayer { Slots = { [EquipSlot.Head] = new SlotEdit() } }, Src, out var reason);
        Assert.Null(draft);
        Assert.Equal("nothing set", reason);
    }

    [Fact]
    public void Equipment_DyeOnlyEditBecomesApplyStainWithoutItem()
    {
        var layer = new EquipmentLayer { Slots = { [EquipSlot.Body] = new SlotEdit { ApplyStains = true, Stain1 = 11, Stain2 = 12 } } };
        var draft = Resolver().Resolve(layer, Src, out _)!;
        var state = GlamourerStateBuilder.Build(draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>()));
        var body  = (JObject)state["Equipment"]!["Body"]!;

        Assert.False(body["Apply"]!.Value<bool>());
        Assert.True(body["ApplyStain"]!.Value<bool>());
        Assert.Equal(11, body["Stain"]!.Value<int>());
        Assert.Equal(12, body["Stain2"]!.Value<int>());
        Assert.Null(body["ItemId"]);
    }

    [Fact]
    public void NothingIds_MatchGlamourersCustomIds()
    {
        Assert.Equal(4294967164ul, NothingItems.IdFor(EquipSlot.Head));
        Assert.Equal(4294967163ul, NothingItems.IdFor(EquipSlot.Body));
        Assert.Equal(4294967159ul, NothingItems.IdFor(EquipSlot.Feet));
        Assert.Equal(4294967155ul, NothingItems.IdFor(EquipSlot.RFinger));
        Assert.Equal(NothingItems.IdFor(EquipSlot.RFinger), NothingItems.IdFor(EquipSlot.LFinger));
        Assert.Null(NothingItems.IdFor(EquipSlot.MainHand));
        Assert.True(NothingItems.IsNothing(4294967164ul));
        Assert.False(NothingItems.IsNothing(10032ul));
    }

    // ---- Appearance --------------------------------------------------------------------------------

    [Fact]
    public void Appearance_CustomizeWetnessAndParametersLandInTheDraft()
    {
        var layer = new AppearanceLayer
        {
            Customize  = { [CustomizeIndex.HairColor] = 60, [CustomizeIndex.Highlights] = 128, [CustomizeIndex.FacialFeature3] = 4 },
            Wetness    = true,
            Parameters =
            {
                ["SkinDiffuse"] = new AdvancedParameter { Color = new Vector3(0.5f, 0.25f, 0.125f) },
                ["LipDiffuse"]  = new AdvancedParameter { Color = new Vector3(1f, 0f, 0f), Alpha = 0.6f },
                ["MuscleTone"]  = new AdvancedParameter { Scalar = 0.75f },
                ["FacePaintUvOffset"] = new AdvancedParameter { Scalar = 0.1f },
                ["NotAParameter"] = new AdvancedParameter(),
            },
        };

        var draft = Resolver().Resolve(layer, Src, out var reason);

        Assert.Null(reason);
        Assert.Equal(60, draft!.Customize[CustomizeIndex.HairColor].Value);
        Assert.Equal(128, draft.Customize[CustomizeIndex.Highlights].Value);
        Assert.Equal(4, draft.Customize[CustomizeIndex.FacialFeature3].Value);
        Assert.True(draft.Meta[MetaIndex.Wetness].Value);
        Assert.Equal(4, draft.Parameters.Count);
        Assert.Equal(0.25f, draft.Parameters["SkinDiffuse"].Value["Green"]!.Value<float>());
        Assert.Null(draft.Parameters["SkinDiffuse"].Value["Alpha"]);
        Assert.Equal(0.6f, draft.Parameters["LipDiffuse"].Value["Alpha"]!.Value<float>());
        Assert.Equal(0.75f, draft.Parameters["MuscleTone"].Value["Percentage"]!.Value<float>());
        Assert.Equal(0.1f, draft.Parameters["FacePaintUvOffset"].Value["Value"]!.Value<float>());
        Assert.False(draft.Parameters.ContainsKey("NotAParameter"));
    }

    [Fact]
    public void Appearance_BuildsApplyFlagsOnlyForSetFields()
    {
        var layer = new AppearanceLayer { Customize = { [CustomizeIndex.HairColor] = 60 }, Parameters = { ["RightEye"] = new AdvancedParameter { Color = Vector3.One } } };
        var draft = Resolver().Resolve(layer, Src, out _)!;
        var state = GlamourerStateBuilder.Build(draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>()));

        Assert.True(state["Customize"]!["HairColor"]!["Apply"]!.Value<bool>());
        Assert.Equal(60, state["Customize"]!["HairColor"]!["Value"]!.Value<int>());
        Assert.Null(state["Customize"]!["Hairstyle"]);
        Assert.True(state["Parameters"]!["RightEye"]!["Apply"]!.Value<bool>());
    }

    [Fact]
    public void Appearance_BodyTypeIsNeverSent()
    {
        var only = new AppearanceLayer { Customize = { [CustomizeIndex.BodyType] = 2 } };
        Assert.Null(Resolver().Resolve(only, Src, out var reason));
        Assert.Equal("nothing set", reason);

        var mixed = new AppearanceLayer { Customize = { [CustomizeIndex.BodyType] = 2, [CustomizeIndex.HairColor] = 5 } };
        var draft = Resolver().Resolve(mixed, Src, out _)!;
        Assert.Equal(CustomizeIndex.HairColor, Assert.Single(draft.Customize).Key);
        Assert.DoesNotContain(CustomizeIndex.BodyType, AppearanceCatalog.EditorOrder);
    }

    [Fact]
    public void Appearance_WithNothingSetIsSkipped()
    {
        Assert.Null(Resolver().Resolve(new AppearanceLayer(), Src, out var reason));
        Assert.Equal("nothing set", reason);
    }

    // ---- cascade and masks -------------------------------------------------------------------------

    private static GlamourerLayer DesignWithHair(FakeGlamourerBridge bridge, int hair, int hairColor, ulong feet)
    {
        var id = Guid.NewGuid();
        bridge.Designs[id] = Build.Design(d =>
        {
            d["Customize"]!["Hairstyle"] = Build.Cust(hair);
            d["Customize"]!["HairColor"] = Build.Cust(hairColor);
            d["Equipment"]!["Feet"]      = Build.Slot(feet);
        });
        bridge.Names[id] = "Design";
        return new GlamourerLayer { DesignId = id };
    }

    [Fact]
    public void AppearanceLayerAfterDesignOverridesOnlyItsFields()
    {
        var bridge     = new FakeGlamourerBridge();
        var appearance = new AppearanceLayer { Customize = { [CustomizeIndex.HairColor] = 99 } };
        var pass       = Build.Pass("look", true, DesignWithHair(bridge, 5, 60, 777), appearance);
        var profile    = new Profile { PassGroups = { Build.Group("Home", true, pass) } };

        var (design, _) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge));

        Assert.Equal(5, design.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.Equal(99, design.Customize[CustomizeIndex.HairColor].Value);
        Assert.Equal(appearance.Id, design.Customize[CustomizeIndex.HairColor].Source.LayerId);
        Assert.Equal(pass.Layers[0].Id, design.Customize[CustomizeIndex.Hairstyle].Source.LayerId);
        Assert.Equal(777ul, design.Items[EquipSlot.Feet].Value.ItemId);
    }

    [Fact]
    public void FinalGroupEquipmentLayerRemovesFeetOverTheDesign()
    {
        var bridge  = new FakeGlamourerBridge();
        var sitting = new EquipmentLayer { Slots = { [EquipSlot.Feet] = new SlotEdit { ItemId = NothingItems.IdFor(EquipSlot.Feet) } } };
        var profile = new Profile
        {
            PassGroups =
            {
                Build.Group("Home", true, Build.Pass("look", true, DesignWithHair(bridge, 5, 60, 777))),
                Build.FinalGroup("Sitting", true, 10, Build.Pass("no shoes", true, sitting)),
            },
        };

        var (design, report) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge));

        Assert.Equal(NothingItems.IdFor(EquipSlot.Feet), design.Items[EquipSlot.Feet].Value.ItemId);
        Assert.Equal(sitting.Id, design.Items[EquipSlot.Feet].Source.LayerId);
        Assert.Equal(5, design.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.Single(design.ActiveFinalGroupIds);
        Assert.Empty(report.SkippedLayers);
    }

    [Fact]
    public void MacroOverrideFiltersEquipmentAndAppearanceLayers()
    {
        var bridge = new FakeGlamourerBridge();
        var pass = Build.Pass("p", true,
            new EquipmentLayer { Slots = { [EquipSlot.Head] = new SlotEdit { ItemId = 1 }, [EquipSlot.Body] = new SlotEdit { ItemId = 2 } }, Meta = { [MetaIndex.HatVisible] = false } },
            new AppearanceLayer { Customize = { [CustomizeIndex.Face] = 2, [CustomizeIndex.HairColor] = 7 }, Wetness = true });
        pass.MacroOverride = new ApplyMask { Equipment = EquipSlotFlags.Head, Customization = CustomizeFlags.HairColor, Meta = false };
        var profile = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, _) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge));

        Assert.Single(design.Items);
        Assert.True(design.Items.ContainsKey(EquipSlot.Head));
        Assert.Single(design.Customize);
        Assert.True(design.Customize.ContainsKey(CustomizeIndex.HairColor));
        Assert.Empty(design.Meta);
    }

    [Fact]
    public void NewLayerKindsAreRegisteredAndMaskable()
    {
        Assert.IsType<EquipmentLayer>(LayerKinds.Create("Equipment"));
        Assert.IsType<AppearanceLayer>(LayerKinds.Create("Appearance"));
        Assert.True(LayerKinds.IsMaskable(new EquipmentLayer()));
        Assert.True(LayerKinds.IsMaskable(new AppearanceLayer()));
        Assert.False(LayerKinds.IsMaskable(new PenumbraAdjustLayer()));
        Assert.False(LayerKinds.IsMaskable(new HonorificLayer()));
    }
}
