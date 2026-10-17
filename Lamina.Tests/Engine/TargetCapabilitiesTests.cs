using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Lamina.Tests.Compose;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Engine;

public class TargetCapabilitiesTests
{
    private static VirtualDesign Everything(Guid moodle)
        => Designs.Of(d =>
        {
            Designs.Head(d, 7);
            Designs.Mod(d, "ModA");
            Designs.Title(d, "Rain Walker");
            Designs.Moodle(d, moodle);
            Designs.Bone(d, "n_hara");
            Designs.Heels(d, 0.1f, relative: true);
            Designs.Decal(d);
        });

    [Fact]
    public void LocalPlayer_TakesEverything_SameInstance()
    {
        var design  = Everything(Guid.NewGuid());
        var dropped = new List<string>();

        var trimmed = TargetCapabilities.Trim(design, TargetKind.LocalPlayer, dropped);

        Assert.Same(design, trimmed);
        Assert.Empty(dropped);
        Assert.All(TargetCapabilities.AllSections, s => Assert.True(TargetCapabilities.Supports(TargetKind.LocalPlayer, s)));
    }

    [Fact]
    public void OtherPlayer_LosesDecals_KeepsTheRest_HeelsBecomeAbsolute()
    {
        var moodle  = Guid.NewGuid();
        var design  = Everything(moodle);
        var dropped = new List<string>();

        var trimmed = TargetCapabilities.Trim(design, TargetKind.OtherPlayer, dropped);

        Assert.NotSame(design, trimmed);
        Assert.False(trimmed.HasDecals);
        Assert.True(trimmed.HasGlamourerContent);
        Assert.True(trimmed.HasMods);
        Assert.NotNull(trimmed.Honorific);
        Assert.True(trimmed.Moodles.ContainsKey(moodle));
        Assert.True(trimmed.HasCustomizePlus);
        Assert.NotNull(trimmed.SimpleHeels);
        Assert.False(trimmed.SimpleHeels!.Value.Value.Relative);
        Assert.Equal(0.1f, trimmed.SimpleHeels.Value.Value.Offset);
        Assert.Equal(2, dropped.Count);
        Assert.Contains(dropped, d => d.StartsWith("Decals skipped"));
        Assert.Contains(dropped, d => d.Contains("relative offset applied as absolute"));
        Assert.NotEqual(design.ContentHash, trimmed.ContentHash);
    }

    [Fact]
    public void Npc_KeepsOnlyGlamourerModsAndCustomizePlus()
    {
        var design  = Everything(Guid.NewGuid());
        var dropped = new List<string>();

        var trimmed = TargetCapabilities.Trim(design, TargetKind.Character, dropped);

        Assert.True(trimmed.HasGlamourerContent);
        Assert.True(trimmed.HasMods);
        Assert.True(trimmed.HasCustomizePlus);
        Assert.Null(trimmed.Honorific);
        Assert.Empty(trimmed.Moodles);
        Assert.False(trimmed.HasDecals);
        Assert.Null(trimmed.SimpleHeels);
        Assert.Equal(4, dropped.Count);
        Assert.Contains(dropped, d => d.StartsWith("Honorific skipped"));
        Assert.Contains(dropped, d => d.StartsWith("Moodles skipped"));
        Assert.Contains(dropped, d => d.StartsWith("Simple Heels skipped"));
        Assert.Contains(dropped, d => d.StartsWith("Decals skipped"));
    }

    [Fact]
    public void Trim_WithNothingToDrop_ReturnsSameInstance()
    {
        var design  = Designs.Of(d => Designs.Head(d, 7));
        var dropped = new List<string>();

        Assert.Same(design, TargetCapabilities.Trim(design, TargetKind.Character, dropped));
        Assert.Empty(dropped);
    }

    [Fact]
    public void EveryUnsupportedSection_HasAReason()
    {
        foreach (var kind in Enum.GetValues<TargetKind>())
            foreach (var section in TargetCapabilities.AllSections)
                Assert.Equal(TargetCapabilities.Supports(kind, section), TargetCapabilities.Reason(kind, section) is null);
    }

    [Fact]
    public void EngineCore_ForAnNpc_NeverCallsHonorificOrHeels_AndReportsDropped()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var glam = Build.Design(d =>
        {
            d["Equipment"]!["Head"] = Build.Slot(3);
            d["Mods"] = new JArray(new JObject { ["Name"] = "ModA", ["Directory"] = "ModA", ["State"] = "Enabled", ["Priority"] = 0 });
        });
        var id = Guid.NewGuid();
        rig.ComposeGlamourer.Designs[id] = glam;
        rig.ComposeGlamourer.Names[id]   = "D";

        var profile = new Profile { Name = "Shared", Enabled = true };
        profile.PassGroups.Add(Build.Group("G", true,
            Build.Pass("Pass", true,
                new GlamourerLayer { DesignId = id },
                new HonorificLayer { Title = "Rain Walker" },
                new SimpleHeelsLayer { Offset = 0.2f })));

        var resolver = Build.Resolver(rig.ComposeGlamourer, customizePlus: rig.CustomizePlus);
        var core     = new EngineCore(new Lamina.Runtime.Compose.PassCompositor(), resolver, rig.Evaluator, rig.Applier, rig.Cleanup, rig.Store, () => rig.Config,
            TargetKind.Character, retainRandom: false);

        var step = core.Step(Designs.Safe(), profile, force: false);

        Assert.NotNull(step.Apply);
        Assert.True(step.Apply!.GlamourerApplied);
        Assert.True(step.Apply.ModsApplied);
        Assert.False(step.Apply.HonorificApplied);
        Assert.False(step.Apply.HeelsApplied);
        Assert.Empty(rig.Honorific.CallsNamed("SetTitle"));
        Assert.Empty(rig.Heels.CallsNamed("RegisterOffset"));
        Assert.Empty(step.Apply.Problems);
        Assert.Equal(2, step.Dropped.Count);
        Assert.Null(step.Design.Honorific);
        Assert.Null(step.Design.SimpleHeels);
        Assert.Equal(step.Design.ContentHash, rig.Store.DesignHash);
    }

    [Fact]
    public void CleanupAbsent_RevertsGlamourerByName_DeletesCustomizePlusById_KeepsTheRest()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d =>
        {
            Designs.Head(d, 1);
            Designs.Title(d, "Rain Walker");
            Designs.Bone(d, "n_hara");
            Designs.Heels(d, 0.1f, relative: false);
        });
        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());
        Assert.True(rig.Store.GlamourerApplied);
        Assert.True(rig.Store.CustomizePlusApplied);
        var tempId = rig.Store.CustomizePlusTemporaryId!.Value;
        rig.Glamourer.ClearCalls();
        rig.CustomizePlus.ClearCalls();

        var result = rig.Cleanup.CleanupAbsent("Some One", "target left");

        Assert.True(result.GlamourerReverted);
        Assert.Single(rig.Glamourer.RevertedByName, r => r.Name == "Some One" && !r.ToAutomation);
        Assert.Empty(rig.Glamourer.CallsNamed("UnlockAndRevertToGame"));
        Assert.True(result.CustomizePlusCleared);
        Assert.Single(rig.CustomizePlus.CallsNamed("DeleteTemporaryProfileById"), c => (Guid)c.Args[0]! == tempId);
        Assert.False(rig.CustomizePlus.Temporary.ContainsKey(0));
        Assert.False(rig.Store.GlamourerApplied);
        Assert.False(rig.Store.CustomizePlusApplied);
        Assert.Null(rig.Store.DesignHash);
        // Honorific and Simple Heels need the actor: still recorded so the next cleanup with the actor present finishes them.
        Assert.NotNull(rig.Store.HonorificSet);
        Assert.NotNull(rig.Store.HeelsSet);
        Assert.True(rig.Store.AnythingApplied);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void CleanupAbsent_ForAnNpc_LeavesGlamourerRecorded()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 1));
        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());

        var result = rig.Cleanup.CleanupAbsent(null, "target left");

        Assert.False(result.GlamourerReverted);
        Assert.Empty(rig.Glamourer.RevertedByName);
        Assert.True(rig.Store.GlamourerApplied);
    }
}
