using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary>
/// Several targets write into one Penumbra collection (by default every target shares Lamina's own): an apply or cleanup of one
/// target must only ever touch the mod settings that target wrote.
/// </summary>
public class SharedCollectionTests
{
    /// <summary> A second target writing into the rig's collection, with its own store, applier and cleanup. </summary>
    private sealed class SecondTarget
    {
        public AppliedStateStore Store { get; } = new();
        public Applier Applier { get; }
        public CleanupService Cleanup { get; }
        public CollectionStatus Status { get; set; } = CollectionStatus.NotAssigned;

        public SecondTarget(EngineRig rig, bool writeWhenUnassigned = true)
        {
            var redraw = new RedrawScheduler(rig.Penumbra, () => 5, rig.Log.Add);
            Applier = new Applier(rig.Glamourer, rig.Penumbra, rig.Honorific, rig.Moodles, rig.CustomizePlus, rig.Heels, null, Store, redraw,
                () => rig.Config, () => rig.Collection, () => Status, () => 5, () => 0x5000, () => rig.Now, rig.Log.Add, null, writeWhenUnassigned);
            Cleanup = new CleanupService(rig.Glamourer, rig.Penumbra, rig.Honorific, rig.Moodles, rig.CustomizePlus, rig.Heels, null, Store, redraw,
                rig.Evaluator, () => rig.Config, () => rig.Collection, () => 5, () => 0x5000, () => rig.Now, rig.Log.Add);
        }
    }

    private static VirtualDesign Mods(params string[] dirs)
        => Designs.Of(d =>
        {
            foreach (var dir in dirs)
                Designs.Mod(d, dir);
        });

    private static ApplyInfo Apply(Applier applier, AppliedStateStore store, VirtualDesign design)
        => applier.Apply(design, DesignDiffer.Diff(store, design, force: false), Designs.Safe());

    [Fact]
    public void TwoTargets_OneCollection_NeitherApplyRemovesTheOthersMods()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Body");
        rig.Penumbra.AddMod("Hair");
        rig.Penumbra.AddMod("Face");
        var other = new SecondTarget(rig);

        Apply(rig.Applier, rig.Store, Mods("Body", "Hair"));
        Apply(other.Applier, other.Store, Mods("Face"));
        var written = rig.Penumbra.TempSettingsIn(rig.Collection);
        Assert.Equal(new[] { "Body", "Face", "Hair" }, written.Keys.OrderBy(k => k));

        // The local target drops Hair: only Hair goes, Face (the other target's) stays.
        Apply(rig.Applier, rig.Store, Mods("Body"));
        written = rig.Penumbra.TempSettingsIn(rig.Collection);
        Assert.Equal(new[] { "Body", "Face" }, written.Keys.OrderBy(k => k));
        Assert.Empty(rig.Penumbra.CallsNamed("RemoveAllTemporaryModSettings"));
        Assert.Single(rig.Penumbra.CallsNamed("RemoveTemporaryModSettings"), c => (string)c.Args[1]! == "Hair");
        Assert.Equal(new[] { "Body" }, rig.Store.ModDirectories.OrderBy(k => k));
        Assert.Equal(new[] { "Face" }, other.Store.ModDirectories.OrderBy(k => k));
    }

    [Fact]
    public void Cleanup_OfOneTarget_LeavesTheOthersMods()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Body");
        rig.Penumbra.AddMod("Face");
        var other = new SecondTarget(rig);
        Apply(rig.Applier, rig.Store, Mods("Body"));
        Apply(other.Applier, other.Store, Mods("Face"));

        var result = other.Cleanup.Cleanup("test", redrawNow: false);

        Assert.True(result.ModsCleared);
        Assert.Empty(result.Problems);
        Assert.Equal(new[] { "Body" }, rig.Penumbra.TempSettingsIn(rig.Collection).Keys);
        Assert.Empty(other.Store.ModDirectories);
        Assert.False(other.Store.ModsApplied);
        Assert.Empty(rig.Penumbra.CallsNamed("RemoveAllTemporaryModSettings"));

        var absent = rig.Cleanup.CleanupAbsent(null, "test");
        Assert.True(absent.ModsCleared);
        Assert.Empty(rig.Penumbra.TempSettingsIn(rig.Collection));
    }

    [Fact]
    public void OtherTarget_WritesModsWhileNotAssigned_LocalTargetDoesNot()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Body");
        var other = new SecondTarget(rig) { Status = CollectionStatus.NotAssigned };

        var info = Apply(other.Applier, other.Store, Mods("Body"));
        Assert.True(info.ModsApplied);
        Assert.Equal(1, info.ModsWritten);
        Assert.True(rig.Penumbra.TempSettingsIn(rig.Collection).ContainsKey("Body"));

        rig.Status = CollectionStatus.NotAssigned;
        var local = Apply(rig.Applier, rig.Store, Mods("Body"));
        Assert.False(local.ModsApplied);
        Assert.Empty(rig.Store.ModDirectories);

        var strict = new SecondTarget(rig, writeWhenUnassigned: false) { Status = CollectionStatus.NotAssigned };
        Assert.False(Apply(strict.Applier, strict.Store, Mods("Body")).ModsApplied);
    }

    [Fact]
    public void RemoveWritten_ForgetsDirectoriesPenumbraNoLongerHas_KeepsRefusedOnes()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Body");
        Apply(rig.Applier, rig.Store, Mods("Body"));
        rig.Store.ModDirectories.Add("Gone");
        var problems = new List<string>();

        Assert.True(TemporaryModSettings.RemoveWritten(rig.Penumbra, rig.Collection, rig.Store, problems));

        Assert.Empty(problems);
        Assert.Empty(rig.Store.ModDirectories);
        Assert.Empty(rig.Penumbra.TempSettingsIn(rig.Collection));

        rig.Store.ModDirectories.Add("Body");
        rig.Penumbra.Available = false;
        Assert.False(TemporaryModSettings.RemoveWritten(rig.Penumbra, rig.Collection, rig.Store, problems));
        Assert.Single(problems);
        Assert.Contains("Body", rig.Store.ModDirectories);
    }
}
