using Xunit;
using Lamina.Ipc;
using Lamina.Runtime;
using Lamina.Runtime.Engine;

namespace Lamina.Tests.Engine;

/// <summary> Differ, applier and cleanup behaviour of the Customize+ (temporary profile) and Simple Heels (registered offset) sections. </summary>
public sealed class CustomizePlusHeelsTests
{
    private static DesignDelta Diff(EngineRig rig, VirtualDesign design, bool force = false)
        => DesignDiffer.Diff(rig.Store, design, force);

    // ---- differ ---------------------------------------------------------------------------------------

    [Fact]
    public void Differ_CustomizePlus_ChangesByBoneContent()
    {
        var rig   = new EngineRig();
        var a     = Designs.Of(d => Designs.Bone(d, "j_kosi", 1.2f));
        var same  = Designs.Of(d => Designs.Bone(d, "j_kosi", 1.2f));
        var other = Designs.Of(d => Designs.Bone(d, "j_kosi", 1.5f));

        rig.Applier.Apply(a, Diff(rig, a), Designs.Safe());
        Assert.False(Diff(rig, same).CustomizePlusChanged);
        Assert.True(Diff(rig, other).CustomizePlusChanged);
        Assert.True(Diff(rig, Designs.Empty()).CustomizePlusChanged);
        Assert.False(Diff(rig, same).RequiresRedraw);
    }

    [Fact]
    public void Differ_Heels_ChangesByOffsetAndMode()
    {
        var rig = new EngineRig();
        var a   = Designs.Of(d => Designs.Heels(d, 0.1f));
        rig.Applier.Apply(a, Diff(rig, a), Designs.Safe());

        Assert.False(Diff(rig, Designs.Of(d => Designs.Heels(d, 0.1f))).HeelsChanged);
        Assert.True(Diff(rig, Designs.Of(d => Designs.Heels(d, 0.2f))).HeelsChanged);
        Assert.True(Diff(rig, Designs.Of(d => Designs.Heels(d, 0.1f, relative: false))).HeelsChanged);
        Assert.True(Diff(rig, Designs.Empty()).HeelsChanged);
    }

    [Fact]
    public void ContentHash_IncludesBonesAndHeels()
    {
        var plain = Designs.Of(d => Designs.Head(d, 1));
        var bones = Designs.Of(d => { Designs.Head(d, 1); Designs.Bone(d, "j_kosi"); });
        var heels = Designs.Of(d => { Designs.Head(d, 1); Designs.Heels(d, 0.05f); });

        Assert.NotEqual(plain.ContentHash, bones.ContentHash);
        Assert.NotEqual(plain.ContentHash, heels.ContentHash);
        Assert.NotEqual(bones.ContentHash, heels.ContentHash);
        Assert.False(bones.IsEmpty);
        Assert.False(heels.IsEmpty);
    }

    // ---- applier --------------------------------------------------------------------------------------

    [Fact]
    public void CustomizePlus_SetsTemporaryProfile_ThenDeletesItWhenDropped()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => { Designs.Bone(d, "j_kosi", 1.2f); Designs.Bone(d, "j_kubi", 0.9f); });

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.True(info.CustomizePlusApplied);
        Assert.Empty(info.Problems);
        Assert.True(rig.Store.CustomizePlusApplied);
        Assert.NotNull(rig.Store.CustomizePlusTemporaryId);
        var (_, bones) = rig.CustomizePlus.Temporary[0];
        Assert.Equal(2, bones.Count);
        Assert.Equal(1.2f, bones["j_kosi"].Scaling.X);
        Assert.False(info.RedrawRequested);

        var empty = Designs.Empty();
        info = rig.Applier.Apply(empty, Diff(rig, empty), Designs.Safe());
        Assert.True(info.CustomizePlusApplied);
        Assert.False(rig.Store.CustomizePlusApplied);
        Assert.False(rig.CustomizePlus.Temporary.ContainsKey(0));
        Assert.Single(rig.CustomizePlus.CallsNamed("DeleteTemporaryProfile"));
    }

    [Fact]
    public void CustomizePlus_Unavailable_IsReportedAndRetriedLater()
    {
        var rig    = new EngineRig();
        rig.CustomizePlus.Available = false;
        var design = Designs.Of(d => Designs.Bone(d, "j_kosi"));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.False(info.CustomizePlusApplied);
        Assert.Contains("Customize+ unavailable", info.Problems);
        Assert.Empty(rig.CustomizePlus.Calls);
        Assert.False(rig.Store.CustomizePlusApplied);
        // The design hash is recorded (nothing transient failed), like Honorific when its plugin is missing.
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);

        rig.CustomizePlus.Available = true;
        rig.Store.DesignHash        = null;
        info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.True(info.CustomizePlusApplied);
        Assert.True(rig.CustomizePlus.Temporary.ContainsKey(0));
    }

    [Fact]
    public void CustomizePlus_SetFailure_LeavesHashUnsetSoTheNextEvaluationRetries()
    {
        var rig = new EngineRig();
        rig.CustomizePlus.SetResult = CustomizePlusEc.InvalidCharacter;
        var design = Designs.Of(d => Designs.Bone(d, "j_kosi"));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.False(info.CustomizePlusApplied);
        Assert.Contains(info.Problems, p => p.Contains("InvalidCharacter"));
        Assert.Null(rig.Store.DesignHash);
        Assert.False(rig.Store.CustomizePlusApplied);
    }

    [Fact]
    public void CustomizePlus_DeleteWhenAlreadyGone_CountsAsCleared()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Bone(d, "j_kosi"));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        rig.CustomizePlus.Temporary.Clear(); // Customize+ reloaded and forgot the temporary profile.

        var empty = Designs.Empty();
        var info  = rig.Applier.Apply(empty, Diff(rig, empty), Designs.Safe());
        Assert.True(info.CustomizePlusApplied);
        Assert.Empty(info.Problems);
        Assert.False(rig.Store.CustomizePlusApplied);
    }

    [Fact]
    public void Heels_Relative_AddsToThePlayersOwnOffset()
    {
        var rig = new EngineRig();
        rig.Heels.LocalOffset = 0.07f;
        var design = Designs.Of(d => Designs.Heels(d, 0.05f));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.True(info.HeelsApplied);
        Assert.Equal(0.12f, rig.Heels.Registered[0], 4);
        Assert.Equal(0.07f, rig.Store.HeelsBase);
        Assert.Equal(new HeelsOffset(0.05f, true), rig.Store.HeelsSet);
        Assert.False(info.RedrawRequested);
    }

    [Fact]
    public void Heels_Absolute_IgnoresThePlayersOwnOffset()
    {
        var rig = new EngineRig();
        rig.Heels.LocalOffset = 0.07f;
        var design = Designs.Of(d => Designs.Heels(d, 0.05f, relative: false));

        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.Equal(0.05f, rig.Heels.Registered[0], 4);
        Assert.Null(rig.Store.HeelsBase);
        Assert.Empty(rig.Heels.CallsNamed("GetLocalOffset"));
    }

    [Fact]
    public void Heels_UnregistersWhenTheDesignDropsIt()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Heels(d, 0.05f));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        var empty = Designs.Empty();
        var info  = rig.Applier.Apply(empty, Diff(rig, empty), Designs.Safe());
        Assert.True(info.HeelsApplied);
        Assert.False(rig.Heels.Registered.ContainsKey(0));
        Assert.Null(rig.Store.HeelsSet);
    }

    [Fact]
    public void Heels_Unavailable_IsReportedWithoutCalls()
    {
        var rig = new EngineRig();
        rig.Heels.Available = false;
        var design = Designs.Of(d => Designs.Heels(d, 0.05f));

        var info = rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        Assert.False(info.HeelsApplied);
        Assert.Contains("Simple Heels unavailable", info.Problems);
        Assert.Empty(rig.Heels.Calls);
    }

    [Fact]
    public void Heels_ForgettingTheRegistration_ReRegistersOnTheNewBase()
    {
        // What LaminaEngine does when Simple Heels reports a changed player offset for a relative registration.
        var rig = new EngineRig();
        rig.Heels.LocalOffset = 0.07f;
        var design = Designs.Of(d => Designs.Heels(d, 0.05f));
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        rig.Heels.LocalOffset = 0.00f;
        rig.Store.HeelsSet    = null;
        rig.Store.HeelsBase   = null;
        rig.Store.DesignHash  = null;

        var delta = Diff(rig, design);
        Assert.True(delta.HeelsChanged);
        Assert.False(delta.CustomizePlusChanged);
        rig.Applier.Apply(design, delta, Designs.Safe());
        Assert.Equal(0.05f, rig.Heels.Registered[0], 4);
        Assert.Equal(0f, rig.Store.HeelsBase);
    }

    // ---- cleanup --------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_UndoesBothSections_OnlyWhenLaminaSetThem()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => { Designs.Bone(d, "j_kosi"); Designs.Heels(d, 0.05f); });
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        rig.CustomizePlus.ClearCalls();
        rig.Heels.ClearCalls();

        var result = rig.Cleanup.Cleanup("test", redrawNow: false);
        Assert.True(result.CustomizePlusCleared);
        Assert.True(result.HeelsCleared);
        Assert.False(rig.CustomizePlus.Temporary.ContainsKey(0));
        Assert.False(rig.Heels.Registered.ContainsKey(0));
        Assert.False(rig.Store.CustomizePlusApplied);
        Assert.Null(rig.Store.HeelsSet);
        Assert.False(rig.Store.AnythingApplied);

        // Nothing of Lamina's left: a second cleanup touches neither plugin.
        rig.CustomizePlus.ClearCalls();
        rig.Heels.ClearCalls();
        result = rig.Cleanup.Cleanup("again", redrawNow: false);
        Assert.False(result.CustomizePlusCleared);
        Assert.False(result.HeelsCleared);
        Assert.Empty(rig.CustomizePlus.Calls);
        Assert.Empty(rig.Heels.Calls);
    }

    [Fact]
    public void Cleanup_ProfileSwitch_UndoesBothSections()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => { Designs.Bone(d, "j_kosi"); Designs.Heels(d, 0.05f); });
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());

        var result = rig.Cleanup.CleanupForProfileSwitch();
        Assert.True(result.CustomizePlusCleared);
        Assert.True(result.HeelsCleared);
        Assert.False(rig.CustomizePlus.Temporary.ContainsKey(0));
        Assert.False(rig.Heels.Registered.ContainsKey(0));
    }

    [Fact]
    public void Cleanup_PluginsGone_ReportsNothingToUndo()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => { Designs.Bone(d, "j_kosi"); Designs.Heels(d, 0.05f); });
        rig.Applier.Apply(design, Diff(rig, design), Designs.Safe());
        rig.CustomizePlus.Available = false;
        rig.Heels.Available         = false;

        var result = rig.Cleanup.Cleanup("test", redrawNow: false);
        Assert.False(result.CustomizePlusCleared);
        Assert.False(result.HeelsCleared);
        Assert.Empty(result.Problems);
        Assert.False(rig.Store.AnythingApplied);
    }

    [Fact]
    public void AnythingApplied_TracksBothSections()
    {
        var store = new AppliedStateStore();
        Assert.False(store.AnythingApplied);
        store.CustomizePlusHash = "x";
        Assert.True(store.AnythingApplied);
        store.Reset();
        Assert.False(store.AnythingApplied);
        store.HeelsSet = new HeelsOffset(0.1f, true);
        Assert.True(store.AnythingApplied);
        store.Reset(keepGlamourer: true);
        Assert.Null(store.HeelsSet);
        Assert.Null(store.CustomizePlusTemporaryId);
    }
}
