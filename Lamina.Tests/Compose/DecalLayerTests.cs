using System.Numerics;
using Lamina.Decals;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> Decal layers: resolution and skip reasons, accumulation across passes, mask immunity and content hashing. </summary>
public class DecalLayerTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
    private readonly PassCompositor _compositor = new();
    private readonly InstantEvaluator _eval = new();

    private static LayerResolver Resolver()
        => Build.Resolver(new FakeGlamourerBridge());

    private static DecalPlacement Placement(DecalSurface surface = DecalSurface.Body, float x = 0.1f, float size = 0.1f, string? part = "chest")
        => new() { Surface = surface, Position = new Vector3(x, 1.2f, 0.05f), Normal = Vector3.UnitZ, Size = size, Rotation = 15f, PartName = part };

    private static DecalLayer Layer(string image = "skull.png", DecalPlacement? placement = null, float opacity = 1f)
        => new() { ImageFile = image, Placement = placement ?? Placement(), Opacity = opacity };

    private static VirtualDesign Freeze(DesignDraft draft)
        => draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());

    private static DesignDraft Draft(params (string Image, DecalPlacement Placement, float Opacity)[] decals)
    {
        var draft = new DesignDraft();
        foreach (var (image, placement, opacity) in decals)
            draft.Decals.Add(new Field<DecalSpec>(new DecalSpec(image, placement, opacity), Src));
        return draft;
    }

    [Fact]
    public void CompleteLayer_ResolvesToOneDecal_WithClonedPlacementAndClampedOpacity()
    {
        var layer = Layer(opacity: 1.5f);

        var draft = Resolver().Resolve(layer, Src, out var reason);

        Assert.Null(reason);
        var field = Assert.Single(draft!.Decals);
        Assert.Equal("skull.png", field.Value.ImageFile);
        Assert.Equal(1f, field.Value.Opacity);
        Assert.Equal(Src, field.Source);
        Assert.Equal(DecalSurface.Body, field.Value.Placement.Surface);
        Assert.Equal("chest", field.Value.Placement.PartName);
        Assert.False(draft.IsEmpty);

        // The frozen spec does not follow later edits of the layer.
        layer.Placement!.Size = 0.5f;
        Assert.Equal(0.1f, field.Value.Placement.Size);

        Assert.Equal(0f, Resolver().Resolve(Layer(opacity: -2f), Src, out _)!.Decals[0].Value.Opacity);
    }

    [Fact]
    public void IncompleteLayers_AreSkipped_WithReasons()
    {
        var resolver = Resolver();

        Assert.Null(resolver.Resolve(new DecalLayer { Placement = Placement() }, Src, out var noImage));
        Assert.Equal("no image chosen", noImage);

        Assert.Null(resolver.Resolve(new DecalLayer { ImageFile = string.Empty, Placement = Placement() }, Src, out var emptyImage));
        Assert.Equal("no image chosen", emptyImage);

        Assert.Null(resolver.Resolve(new DecalLayer { ImageFile = "skull.png" }, Src, out var notPlaced));
        Assert.Equal("not placed", notPlaced);

        Assert.Null(resolver.Resolve(new DecalLayer { ImageFile = "skull.png", Placement = Placement(), Enabled = false }, Src, out var disabled));
        Assert.Equal("disabled", disabled);
    }

    [Fact]
    public void TwoLayersInTwoPasses_MergeInOrder_AndIgnoreTheMask()
    {
        var first  = Layer("a.png", Placement(DecalSurface.Body));
        var second = Layer("b.png", Placement(DecalSurface.Face));
        var passA  = Build.Pass("A", true, first);
        var passB  = Build.Pass("B", true, second);
        passB.MacroOverride = ApplyMask.OnlyMods();
        var profile = new Profile { PassGroups = { Build.Group("G", true, passA, passB) } };

        var (design, report) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Resolver());

        Assert.Equal(new[] { "a.png", "b.png" }, design.Decals.Select(f => f.Value.ImageFile));
        Assert.Equal(first.Id, design.Decals[0].Source.LayerId);
        Assert.Equal(second.Id, design.Decals[1].Source.LayerId);
        Assert.True(design.HasDecals);
        Assert.False(design.IsEmpty);
        Assert.Empty(report.SkippedLayers);

        var (reversed, _) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.BottomToTop, Resolver());
        Assert.Equal(new[] { "b.png", "a.png" }, reversed.Decals.Select(f => f.Value.ImageFile));
    }

    [Fact]
    public void SkippedDecalLayer_IsReported_AndContributesNothing()
    {
        var profile = new Profile { PassGroups = { Build.Group("G", true, Build.Pass("P", true, new DecalLayer { ImageFile = "skull.png" })) } };

        var (design, report) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Resolver());

        Assert.True(design.IsEmpty);
        var (_, reason) = Assert.Single(report.SkippedLayers);
        Assert.Equal("not placed", reason);
    }

    [Fact]
    public void Filter_PassesDecalsThrough()
    {
        var draft = Draft(("a.png", Placement(), 1f));
        draft.Items[EquipSlot.Head] = new Field<EquipItem>(new EquipItem(1), Src);

        var filtered = draft.Filter(ApplyMask.OnlyMods());

        Assert.Empty(filtered.Items);
        Assert.Single(filtered.Decals);
        Assert.False(LayerKinds.IsMaskable(new DecalLayer()));
    }

    [Fact]
    public void DesignHash_ChangesWithImagePlacementOpacityAndOrder_NotWithProvenanceOrInformationalFields()
    {
        var baseline = Freeze(Draft(("a.png", Placement(), 1f))).ContentHash;

        Assert.Equal(baseline, Freeze(Draft(("a.png", Placement(), 1f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(Draft(("b.png", Placement(), 1f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(Draft(("a.png", Placement(x: 0.2f), 1f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(Draft(("a.png", Placement(size: 0.3f), 1f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(Draft(("a.png", Placement(DecalSurface.Face), 1f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(Draft(("a.png", Placement(), 0.5f))).ContentHash);
        Assert.NotEqual(baseline, Freeze(new DesignDraft()).ContentHash);

        var ab = Freeze(Draft(("a.png", Placement(), 1f), ("b.png", Placement(), 1f))).ContentHash;
        var ba = Freeze(Draft(("b.png", Placement(), 1f), ("a.png", Placement(), 1f))).ContentHash;
        Assert.NotEqual(ab, ba);
        Assert.NotEqual(baseline, ab);

        // Provenance and the informational pick data (part name, uv, mirroring) are not content.
        var other = new DesignDraft();
        var placement = Placement(part: "arm");
        placement.Uv         = new Vector2(0.3f, 0.7f);
        placement.MirroredUv = true;
        other.Decals.Add(new Field<DecalSpec>(new DecalSpec("a.png", placement, 1f), new Provenance(Guid.NewGuid(), null, null, "elsewhere")));
        Assert.Equal(baseline, Freeze(other).ContentHash);
    }

    [Fact]
    public void Explainer_ListsDecals()
    {
        var design = Freeze(Draft(("skull.png", Placement(), 0.75f)));

        var text = DesignExplainer.Explain(design, _ => "G", _ => "P");

        Assert.Contains("Decal skull.png", text);
        Assert.Contains("Body / chest", text);
        Assert.Contains("75", text);
        Assert.DoesNotContain("Design is empty", text);
    }
}
