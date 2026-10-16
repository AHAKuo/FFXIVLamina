using System.Numerics;
using Lamina.Decals;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Lamina.Tests.Compose;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary> Decals through the engine: differ flags, store bookkeeping, the applier's hand-off to the publisher, and cleanup. </summary>
public class DecalApplyTests
{
    private static DesignDelta Diff(EngineRig rig, VirtualDesign design)
        => DesignDiffer.Diff(rig.Store, design, force: false);

    // ---- differ / store ----------------------------------------------------------------------------

    [Fact]
    public void Differ_FlagsDecals_OnlyWhenTheListChanges()
    {
        var store  = new AppliedStateStore();
        var design = Designs.Of(d => Designs.Decal(d));

        var delta = DesignDiffer.Diff(store, design, false);
        Assert.True(delta.DecalsChanged);
        Assert.False(delta.NoChange);
        Assert.False(delta.ModsChanged);
        // The decal redraw is requested when the publisher completes, not by the delta.
        Assert.False(delta.RequiresRedraw);

        store.DecalsHash = DesignDiffer.DecalsHash(design);
        Assert.False(DesignDiffer.Diff(store, design, false).DecalsChanged);
        Assert.True(DesignDiffer.Diff(store, design, false).NoChange);

        Assert.True(DesignDiffer.Diff(store, Designs.Of(d => Designs.Decal(d, x: 0.3f)), false).DecalsChanged);
        Assert.True(DesignDiffer.Diff(store, Designs.Of(d => Designs.Decal(d, opacity: 0.5f)), false).DecalsChanged);
        Assert.True(DesignDiffer.Diff(store, Designs.Of(d => Designs.Decal(d, "other.png")), false).DecalsChanged);
        Assert.True(DesignDiffer.Diff(store, Designs.Of(d => { Designs.Decal(d); Designs.Decal(d, "second.png"); }), false).DecalsChanged);

        // Losing the decals is a change too, and a forced apply always republishes.
        Assert.True(DesignDiffer.Diff(store, Designs.Empty(), false).DecalsChanged);
        store.DesignHash = design.ContentHash;
        Assert.True(DesignDiffer.Diff(store, design, false).NoChange);
        Assert.True(DesignDiffer.Diff(store, design, true).DecalsChanged);
    }

    [Fact]
    public void DecalsHash_IsOrderSensitive_AndIgnoresProvenance()
    {
        var ab = Designs.Of(d => { Designs.Decal(d, "a.png"); Designs.Decal(d, "b.png"); });
        var ba = Designs.Of(d => { Designs.Decal(d, "b.png"); Designs.Decal(d, "a.png"); });
        var other = new DesignDraft();
        other.Decals.Add(new Field<DecalSpec>(ab.Decals[0].Value, new Provenance(Guid.NewGuid(), null, null, "elsewhere")));
        other.Decals.Add(new Field<DecalSpec>(ab.Decals[1].Value, Provenance.None));

        Assert.NotEqual(DesignDiffer.DecalsHash(ab), DesignDiffer.DecalsHash(ba));
        Assert.Equal(DesignDiffer.DecalsHash(ab), DesignDiffer.DecalsHash(other.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>())));
        Assert.NotEqual(DesignDiffer.DecalsHash(ab), DesignDiffer.ModsHash(ab));
    }

    [Fact]
    public void Store_TracksDecals_AndResetClearsThem()
    {
        var store = new AppliedStateStore();
        Assert.False(store.DecalsApplied);
        Assert.False(store.AnythingApplied);

        store.DecalsHash = "abc";
        Assert.True(store.DecalsApplied);
        Assert.True(store.AnythingApplied);

        store.PendingDecals = new[] { Designs.Of(d => Designs.Decal(d)).Decals[0].Value };
        store.DecalsDrawKey = "outfit";
        store.Reset(keepGlamourer: true);
        Assert.Null(store.DecalsHash);
        Assert.Null(store.PendingDecals);
        Assert.Null(store.DecalsDrawKey);
        Assert.False(store.AnythingApplied);

        store.DecalsHash    = "abc";
        store.PendingDecals = Array.Empty<DecalSpec>();
        store.DecalsDrawKey = "outfit";
        store.ForgetDecals();
        Assert.Null(store.DecalsHash);
        Assert.Null(store.PendingDecals);
        Assert.Null(store.DecalsDrawKey);
    }

    // ---- applier -----------------------------------------------------------------------------------

    [Fact]
    public void DecalsPresent_PublishesTheSpecs_AndRecordsTheHash()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d =>
        {
            Designs.Decal(d, "a.png");
            Designs.Decal(d, "b.png", DecalSurface.Face);
        });

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        var publish = Assert.Single(rig.Decals!.Publishes);
        Assert.Equal(rig.Collection, publish.Collection);
        Assert.Equal(0, publish.ObjectIndex);
        Assert.Equal(new[] { "a.png", "b.png" }, publish.Decals.Select(s => s.ImageFile));
        Assert.Same(design.Decals[0].Value, publish.Decals[0]);
        Assert.Empty(rig.Decals.Clears);

        Assert.True(info.DecalsPublished);
        Assert.False(info.DecalsDeferred);
        Assert.False(info.RedrawRequested);
        Assert.Empty(info.Problems);
        Assert.Null(publish.ModsKey);
        Assert.Equal(DesignDiffer.DecalsHash(design), rig.Store.DecalsHash);
        Assert.Equal("outfit-a", rig.Store.DecalsDrawKey);
        Assert.Null(rig.Store.PendingDecals);
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
        Assert.True(Diff(rig, design).NoChange);
    }

    [Fact]
    public void ModsAndDecals_DeferThePublish_UntilAfterTheRedraw()
    {
        // The publisher would snapshot the outfit still on screen; the redraw for the mods replaces it first.
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Skin");
        var design = Designs.Of(d =>
        {
            Designs.Mod(d, "Skin");
            Designs.Decal(d);
        });

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        Assert.Empty(rig.Decals!.Publishes);
        Assert.True(info.DecalsDeferred);
        Assert.False(info.DecalsPublished);
        Assert.True(info.RedrawRequested);
        Assert.True(rig.Redraw.Pending);
        Assert.Empty(info.Problems);
        Assert.NotNull(rig.Store.PendingDecals);
        Assert.Same(design.Decals[0].Value, Assert.Single(rig.Store.PendingDecals));
        Assert.Equal(DesignDiffer.DecalsHash(design), rig.Store.DecalsHash);
        Assert.True(rig.Store.DecalsApplied);
        Assert.Null(rig.Store.DecalsDrawKey);
        Assert.True(Diff(rig, design).NoChange);
        Assert.Contains(rig.Log, m => m.Contains("Decals deferred"));
    }

    [Fact]
    public void RedrawAlreadyPending_DefersEvenADecalOnlyChange()
    {
        var rig = new EngineRig();
        rig.Redraw.Request();
        var design = Designs.Of(d => Designs.Decal(d));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        Assert.Empty(rig.Decals!.Publishes);
        Assert.True(info.DecalsDeferred);
        Assert.NotNull(rig.Store.PendingDecals);
    }

    [Fact]
    public void ImmediatePublish_PassesTheModsKey_AndClearsAnyPending()
    {
        // The mods are already in the collection (same hash), so only the decal change is applied: published now, with the mods key.
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Skin");
        var design = Designs.Of(d =>
        {
            Designs.Mod(d, "Skin");
            Designs.Decal(d);
        });
        rig.Store.ModsHash      = DesignDiffer.ModsHash(design);
        rig.Store.PendingDecals = Array.Empty<DecalSpec>();

        var delta = Diff(rig, design);
        Assert.False(delta.ModsChanged);
        var info = rig.Applier.Apply(design, delta, Designs.Safe());

        Assert.True(info.DecalsPublished);
        Assert.Equal(DesignDiffer.ModsHash(design), Assert.Single(rig.Decals!.Publishes).ModsKey);
        Assert.Null(rig.Store.PendingDecals);
    }

    [Fact]
    public void DesignLosesDecals_ClearsAndRequestsARedraw()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Decal(d));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        var empty = Designs.Empty();
        var info  = rig.Applier.Apply(empty, Diff(rig, empty), Designs.Safe());

        Assert.Equal(new[] { rig.Collection }, rig.Decals!.Clears);
        Assert.Single(rig.Decals.Publishes);
        Assert.Null(rig.Store.DecalsHash);
        Assert.False(info.DecalsPublished);
        Assert.True(info.RedrawRequested);
        Assert.True(rig.Redraw.Pending);
        Assert.Empty(info.Problems);
    }

    [Fact]
    public void DesignWithoutDecals_NeverTouchesThePublisher()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 1));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        Assert.Empty(rig.Decals!.Publishes);
        Assert.Empty(rig.Decals.Clears);
        Assert.False(info.DecalsPublished);
        Assert.False(info.RedrawRequested);
    }

    [Fact]
    public void CollectionNotAssigned_SkipsDecals_WithoutAProblem()
    {
        var rig    = new EngineRig { Status = CollectionStatus.NotAssigned };
        var design = Designs.Of(d =>
        {
            Designs.Head(d, 1);
            Designs.Decal(d);
        });

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        Assert.Empty(rig.Decals!.Publishes);
        Assert.Null(rig.Store.DecalsHash);
        Assert.False(info.DecalsPublished);
        Assert.Empty(info.Problems);
        Assert.True(info.GlamourerApplied);
        Assert.Contains(rig.Log, m => m.Contains("Decals skipped"));
    }

    [Fact]
    public void NoPublisher_IsAProblem_NotARetry()
    {
        var rig    = new EngineRig(decalPublisher: false);
        var design = Designs.Of(d => Designs.Decal(d));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        Assert.Contains("decals unavailable", info.Problems);
        Assert.False(info.DecalsPublished);
        Assert.Null(rig.Store.DecalsHash);
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
        Assert.True(Diff(rig, design).NoChange);

        // A design without decals never mentions the missing feature.
        var plain = Designs.Of(d => Designs.Head(d, 1));
        Assert.Empty(rig.Applier.Apply(plain, Diff(rig, plain), Designs.Safe()).Problems);
    }

    [Fact]
    public void ModsChange_RepublishesUnchangedDecals()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Skin");
        rig.Penumbra.AddMod("OtherSkin");
        var first = Designs.Of(d =>
        {
            Designs.Mod(d, "Skin");
            Designs.Decal(d);
        });
        Assert.True(rig.Applier.Apply(first, Diff(rig, first), Designs.Safe()).DecalsDeferred);
        // The redraw went out and the follow-up published the pending decals.
        rig.Redraw.Cancel();
        rig.Store.PendingDecals = null;
        rig.Store.DecalsDrawKey = "outfit-a";

        var second = Designs.Of(d =>
        {
            Designs.Mod(d, "OtherSkin");
            Designs.Decal(d);
        });
        var delta = Diff(rig, second);
        Assert.True(delta.ModsChanged);
        Assert.False(delta.DecalsChanged);

        var info = rig.Applier.Apply(second, delta, Designs.Safe());

        // Republished (the other mod may change the skin texture), but only after the mods redraw: the decals wait in the store.
        Assert.Empty(rig.Decals!.Publishes);
        Assert.False(info.DecalsPublished);
        Assert.True(info.DecalsDeferred);
        Assert.True(info.RedrawRequested);
        Assert.NotNull(rig.Store.PendingDecals);
        Assert.Equal(DesignDiffer.ModsHash(second), rig.Store.ModsHash);
    }

    [Fact]
    public void ForcedApply_RepublishesDecals()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Decal(d));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, force: true), Designs.Safe());

        Assert.Equal(2, rig.Decals!.Publishes.Count);
        Assert.Equal(DesignDiffer.DecalsHash(design), rig.Store.DecalsHash);
    }

    [Fact]
    public void PublishFailure_ForgottenByTheEngine_IsRepublishedOnTheNextApply()
    {
        // The engine's completion handler nulls DecalsHash and DesignHash on failure; the next evaluation then re-diffs.
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Decal(d));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        rig.Store.DecalsHash = null;
        rig.Store.DesignHash = null;

        var delta = Diff(rig, design);
        Assert.True(delta.DecalsChanged);
        Assert.False(delta.GlamourerChanged);
        Assert.False(delta.ModsChanged);

        rig.Applier.Apply(design, delta, Designs.Safe());
        Assert.Equal(2, rig.Decals!.Publishes.Count);
        Assert.Empty(rig.Glamourer.CallsNamed("ApplyState"));
    }

    [Fact]
    public void EngineCore_ComposesDecalLayers_AndPublishesThem()
    {
        var rig       = new EngineRig();
        var placement = new DecalPlacement { Surface = DecalSurface.Face, Position = new Vector3(0, 1.6f, 0.1f), Normal = Vector3.UnitZ, Size = 0.05f };
        var layer     = new DecalLayer { ImageFile = "tear.png", Placement = placement, Opacity = 0.8f };
        var profile   = new Profile { Name = "P", Enabled = true };
        profile.PassGroups.Add(Build.Group("G", true, Build.Pass("Pass", true, layer)));

        var step = rig.Core.Step(Designs.Safe(), profile, force: false);

        Assert.True(step.Design.HasDecals);
        Assert.NotNull(step.Apply);
        Assert.True(step.Apply!.DecalsPublished);
        var publish = Assert.Single(rig.Decals!.Publishes);
        var spec    = Assert.Single(publish.Decals);
        Assert.Equal("tear.png", spec.ImageFile);
        Assert.Equal(DecalSurface.Face, spec.Placement.Surface);
        Assert.Equal(0.8f, spec.Opacity);
        Assert.NotSame(placement, spec.Placement);

        Assert.True(rig.Core.Step(Designs.Safe(), profile, force: false).Delta.NoChange);
        Assert.Single(rig.Decals.Publishes);
    }

    // ---- cleanup -----------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_ClearsPublishedDecals_AndRedraws()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Decal(d));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.Equal(new[] { rig.Collection }, rig.Decals!.Clears);
        Assert.True(result.DecalsCleared);
        Assert.True(result.RedrawSent);
        Assert.Empty(result.Problems);
        Assert.False(rig.Store.DecalsApplied);
        Assert.False(rig.Store.AnythingApplied);
    }

    [Fact]
    public void Cleanup_WithoutPublishedDecals_LeavesThePublisherAlone()
    {
        var rig = new EngineRig();

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.Empty(rig.Decals!.Clears);
        Assert.False(result.DecalsCleared);
        Assert.False(result.RedrawSent);
    }

    [Fact]
    public void ProfileSwitch_ClearsDecals_ThenTheNewDesignRepublishes()
    {
        var rig = new EngineRig();
        var a   = new Profile { Name = "A", Enabled = true };
        var b   = new Profile { Name = "B", Enabled = true };
        a.PassGroups.Add(Build.Group("G", true, Build.Pass("P", true, new DecalLayer { ImageFile = "a.png", Placement = new DecalPlacement() })));
        b.PassGroups.Add(Build.Group("G", true, Build.Pass("P", true, new DecalLayer { ImageFile = "b.png", Placement = new DecalPlacement() })));

        rig.Core.Step(Designs.Safe(), a, false);
        var step = rig.Core.Step(Designs.Safe(), b, false);

        Assert.True(step.ProfileSwitched);
        Assert.Equal(new[] { rig.Collection }, rig.Decals!.Clears);
        // The switch cleanup asked for a redraw, so B's decals wait for it instead of being baked onto the outgoing outfit.
        Assert.Equal(new[] { "a.png" }, rig.Decals.Publishes.Select(p => p.Decals[0].ImageFile));
        Assert.True(step.Apply!.DecalsDeferred);
        Assert.Equal("b.png", Assert.Single(rig.Store.PendingDecals!).ImageFile);
        Assert.True(rig.Store.DecalsApplied);
    }
}
