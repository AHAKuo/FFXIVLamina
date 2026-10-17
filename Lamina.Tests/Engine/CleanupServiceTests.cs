using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Xunit;

namespace Lamina.Tests.Engine;

public class CleanupServiceTests
{
    private static EngineRig ApplyFull(out VirtualDesign design, out Guid moodle, out Guid preset)
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var id = rig.Moodles.AddMoodle("A");
        var ps = rig.Moodles.AddPreset("P", rig.Moodles.AddMoodle("B"));
        moodle = id;
        preset = ps;
        var d = Designs.Of(x =>
        {
            Designs.Head(x, 1);
            Designs.Mod(x, "ModA");
            Designs.Title(x, "Rain Walker");
            Designs.Moodle(x, id);
            Designs.Moodle(x, ps, MoodleOp.AddPreset);
        });
        design = d;
        rig.Applier.Apply(d, DesignDiffer.Diff(rig.Store, d, false), Designs.Safe());
        rig.Redraw.Tick(Designs.Safe(), RedrawPolicy.Immediate);
        rig.Glamourer.ClearCalls();
        rig.Penumbra.ClearCalls();
        rig.Honorific.ClearCalls();
        rig.Moodles.ClearCalls();
        return rig;
    }

    [Fact]
    public void UndoesEverythingLaminaDid_AndResetsStore()
    {
        var rig = ApplyFull(out _, out var moodle, out var preset);

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.True(result.ModsCleared);
        Assert.Empty(rig.Penumbra.TempSettingsIn(rig.Collection));
        Assert.True(result.GlamourerReverted);
        Assert.Single(rig.Glamourer.CallsNamed("UnlockAndRevertToGame"));
        Assert.Empty(rig.Glamourer.CallsNamed("UnlockAndRevertToAutomation"));
        Assert.True(result.HonorificCleared);
        Assert.False(rig.Honorific.Titles.ContainsKey(0));
        Assert.Equal(1, result.MoodlesRemoved);
        Assert.Equal(1, result.PresetsRemoved);
        Assert.DoesNotContain(moodle, rig.Moodles.ActiveOn(rig.PlayerAddress));
        Assert.DoesNotContain(preset, rig.Moodles.ActivePresets[rig.PlayerAddress]);
        Assert.True(result.RedrawSent);
        Assert.Empty(result.Problems);

        Assert.Null(rig.Store.DesignHash);
        Assert.False(rig.Store.GlamourerApplied);
        Assert.False(rig.Store.ModsApplied);
        Assert.Null(rig.Store.HonorificSet);
        Assert.Empty(rig.Store.MoodlesAdded);
        Assert.Empty(rig.Store.PresetsApplied);
        Assert.False(rig.Store.AnythingApplied);
    }

    [Fact]
    public void NothingApplied_TouchesNothing()
    {
        var rig = new EngineRig();

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.Empty(rig.Glamourer.Calls);
        Assert.Empty(rig.Honorific.Calls);
        Assert.Empty(rig.Moodles.Calls);
        // Nothing recorded, nothing removed: other targets may share the collection, so there is no blanket clear.
        Assert.Empty(rig.Penumbra.CallsNamed("RemoveAllTemporaryModSettings"));
        Assert.Empty(rig.Penumbra.CallsNamed("RemoveTemporaryModSettings"));
        Assert.False(result.ModsCleared);
        Assert.False(result.RedrawSent);
    }

    [Fact]
    public void NeverTouchesCollectionAssignments()
    {
        var rig = ApplyFull(out _, out _, out _);

        rig.Cleanup.Cleanup("test", redrawNow: true);

        // The player manages assignments in Penumbra; Lamina only reads them.
        Assert.DoesNotContain(rig.Penumbra.Calls, c => c.Name.Contains("SetCollection"));
        Assert.True(rig.Penumbra.ObjectCollections.ContainsKey(0));
    }

    [Fact]
    public void HonorificNotSetByLamina_IsLeftAlone()
    {
        var rig = new EngineRig();
        rig.Honorific.Titles[0] = new HonorificTitle(false, "User's own", false, null, null);
        var design = Designs.Of(d => Designs.Head(d, 1));
        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());

        rig.Cleanup.Cleanup("test", redrawNow: false);

        Assert.True(rig.Honorific.Titles.ContainsKey(0));
    }

    [Fact]
    public void DeferredRedraw_WhenNotNow_AndPolicyDefers()
    {
        var rig = ApplyFull(out _, out _, out _);
        rig.Config.RedrawPolicy = RedrawPolicy.DeferWhenUnsafe;

        var result = rig.Cleanup.Cleanup("test", redrawNow: false);

        Assert.False(result.RedrawSent);
        Assert.True(rig.Redraw.Pending);
    }

    [Fact]
    public void UnavailablePlugins_AreSkipped_WithoutProblems()
    {
        var rig = ApplyFull(out _, out _, out _);
        rig.Glamourer.Available = false;
        rig.Honorific.Available = false;
        rig.Moodles.Available   = false;
        rig.Penumbra.Available  = false;

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.Empty(result.Problems);
        Assert.False(result.GlamourerReverted);
        Assert.Null(rig.Store.DesignHash);
    }

    [Fact]
    public void FullCleanup_ResetsEvaluatorTimers()
    {
        var rig = ApplyFull(out _, out _, out _);

        rig.Cleanup.Cleanup("test", redrawNow: false);

        Assert.Equal(1, rig.Evaluator.Resets);
    }

    [Fact]
    public void ProfileSwitch_KeepsGlamourerAndTimers_ClearsTheRest()
    {
        var rig = ApplyFull(out _, out var moodle, out _);

        var result = rig.Cleanup.CleanupForProfileSwitch();

        Assert.True(result.ModsCleared);
        Assert.Empty(rig.Penumbra.TempSettingsIn(rig.Collection));
        Assert.False(result.GlamourerReverted);
        Assert.Empty(rig.Glamourer.Calls);
        Assert.True(rig.Store.GlamourerApplied);
        Assert.True(result.HonorificCleared);
        Assert.DoesNotContain(moodle, rig.Moodles.ActiveOn(rig.PlayerAddress));
        Assert.Null(rig.Store.DesignHash);
        Assert.False(rig.Store.ModsApplied);
        Assert.Equal(0, rig.Evaluator.Resets);
    }
}
