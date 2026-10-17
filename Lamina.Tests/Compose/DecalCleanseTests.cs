using System.Numerics;
using Lamina.Decals;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> The Decal Cleanse layer drops every decal painted before it, within a pass and across passes, and leaves the rest alone. </summary>
public class DecalCleanseTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
    private readonly PassCompositor _compositor = new();
    private readonly InstantEvaluator _eval = new();

    private static LayerResolver Resolver()
        => Build.Resolver(new FakeGlamourerBridge());

    private static DecalPlacement Placement(DecalSurface surface = DecalSurface.Body)
        => new() { Surface = surface, Position = new Vector3(0.1f, 1.2f, 0.05f), Normal = Vector3.UnitZ, Size = 0.1f, Rotation = 0f, PartName = "chest" };

    private static DecalLayer Decal(string image)
        => new() { ImageFile = image, Placement = Placement() };

    private (VirtualDesign Design, EvaluationReport Report) Compose(Profile profile, EvaluationDirection direction = EvaluationDirection.TopToBottom)
        => _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, direction, Resolver());

    private static Profile OnePass(params Layer[] layers)
        => new() { PassGroups = { Build.Group("G", true, Build.Pass("p", true, layers)) } };

    [Fact]
    public void Resolves_ToACleanseMarker_WithTheLayerAsSource()
    {
        var layer = new DecalCleanseLayer();

        var draft = Resolver().Resolve(layer, Src, out var reason);

        Assert.Null(reason);
        Assert.NotNull(draft);
        Assert.Empty(draft!.Decals);
        Assert.True(draft.DecalCleanse!.Value.Value);
        Assert.Equal(Src, draft.DecalCleanse.Value.Source);
        Assert.False(draft.IsEmpty);

        Assert.Null(Resolver().Resolve(new DecalCleanseLayer { Enabled = false }, Src, out var disabled));
        Assert.Equal("disabled", disabled);
    }

    [Fact]
    public void WithinAPass_DropsTheDecalsAboveIt_AndKeepsTheOnesBelow()
    {
        var cleanse = new DecalCleanseLayer();
        var after   = Decal("after.png");
        var (design, report) = Compose(OnePass(Decal("a.png"), Decal("b.png"), cleanse, after));

        Assert.Equal(new[] { "after.png" }, design.Decals.Select(f => f.Value.ImageFile));
        Assert.Equal(after.Id, design.Decals[0].Source.LayerId);
        Assert.Equal(cleanse.Id, design.DecalCleanse!.Value.Source.LayerId);
        Assert.Empty(report.SkippedLayers);
    }

    [Fact]
    public void AcrossPasses_ACleansingPass_WipesTheEarlierPasses_ButNotTheLaterOnes()
    {
        var profile = new Profile
        {
            PassGroups =
            {
                Build.Group("G", true,
                    Build.Pass("first",  true, Decal("a.png")),
                    Build.Pass("second", true, Decal("b.png"), new DecalCleanseLayer(), Decal("c.png")),
                    Build.Pass("third",  true, Decal("d.png"))),
            },
        };

        var (design, _) = Compose(profile);

        Assert.Equal(new[] { "c.png", "d.png" }, design.Decals.Select(f => f.Value.ImageFile));
        Assert.NotNull(design.DecalCleanse);
    }

    [Fact]
    public void BottomToTop_FollowsTheEvaluationDirection()
    {
        // Bottom to top the rows merge in reverse: "below" wins, so the cleanse drops the decals listed after it and keeps those before it.
        var (design, _) = Compose(OnePass(Decal("kept.png"), new DecalCleanseLayer(), Decal("dropped.png")), EvaluationDirection.BottomToTop);

        Assert.Equal(new[] { "kept.png" }, design.Decals.Select(f => f.Value.ImageFile));
    }

    [Fact]
    public void InAFinalGroup_StripsTheNormalGroupsDecals()
    {
        var profile = new Profile
        {
            PassGroups =
            {
                Build.Group("Normal", true, Build.Pass("outfit", true, Decal("a.png"), Decal("b.png"))),
                Build.Group("Final", true, Build.Pass("strip", true, new DecalCleanseLayer())),
            },
        };
        profile.PassGroups[1].IsFinal = true;

        var (design, _) = Compose(profile);

        Assert.Empty(design.Decals);
        Assert.False(design.HasDecals);
        Assert.NotNull(design.DecalCleanse);
        Assert.False(design.IsEmpty); // a preview of the stripping pass is not "nothing"
    }

    [Fact]
    public void CleanseAlone_LeavesEveryOtherSectionAlone_AndHashesLikeNoDecals()
    {
        var dyed = new EquipmentLayer();
        dyed.Slots[EquipSlot.Body] = new SlotEdit { ItemId = 3001, ApplyStains = true, Stain1 = 5, Stain2 = 6 };
        var (withCleanse, _)    = Compose(OnePass(dyed, Decal("a.png"), new DecalCleanseLayer()));
        var (withoutDecals, _)  = Compose(OnePass(dyed));

        Assert.Equal(3001ul, withCleanse.Items[EquipSlot.Body].Value.ItemId);
        Assert.Equal(new Stains(5, 6), withCleanse.Stains[EquipSlot.Body].Value);
        Assert.Empty(withCleanse.Decals);
        // The decal list already reflects the wipe; the marker itself is not content, so nothing is re-applied for it.
        Assert.Equal(withoutDecals.ContentHash, withCleanse.ContentHash);
    }

    [Fact]
    public void IgnoresTheMacroOverride_LikeDecals()
    {
        var pass = Build.Pass("p", true, Decal("a.png"), new DecalCleanseLayer(), Decal("b.png"));
        pass.MacroOverride = ApplyMask.OnlyMods();
        var profile = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, _) = Compose(profile);

        Assert.Equal(new[] { "b.png" }, design.Decals.Select(f => f.Value.ImageFile));
        Assert.False(LayerKinds.IsMaskable(new DecalCleanseLayer()));

        // Filter carries the marker even when it is asked to (defensive: the compositor never masks this kind).
        var draft = new DesignDraft { DecalCleanse = new Field<bool>(true, Src) };
        Assert.NotNull(draft.Filter(ApplyMask.OnlyMods()).DecalCleanse);
    }

    [Fact]
    public void Explainer_NamesTheCleanse_AndWhatSurvivedIt()
    {
        var (stripped, _) = Compose(OnePass(Decal("a.png"), new DecalCleanseLayer()));
        var (partial, _)  = Compose(OnePass(Decal("a.png"), new DecalCleanseLayer(), Decal("b.png")));

        var strippedText = DesignExplainer.Explain(stripped, _ => "G", _ => "P");
        Assert.Contains("Decal cleanse", strippedText);
        Assert.Contains("none after it", strippedText);
        Assert.DoesNotContain("Design is empty", strippedText);

        var partialText = DesignExplainer.Explain(partial, _ => "G", _ => "P");
        Assert.Contains("1 after it", partialText);
        Assert.Contains("Decal b.png", partialText);
        Assert.DoesNotContain("Decal a.png", partialText);
    }

    [Fact]
    public void RoundTripsThroughTheLayerRegistry()
    {
        var layer = LayerKinds.Create(DecalCleanseLayer.KindName);
        Assert.IsType<DecalCleanseLayer>(layer);
        Assert.Equal("Decal cleanse", layer!.Describe());
        Assert.Contains(LayerKinds.All, k => k.Kind == DecalCleanseLayer.KindName && k.DisplayName == "Decal Cleanse");
    }
}
