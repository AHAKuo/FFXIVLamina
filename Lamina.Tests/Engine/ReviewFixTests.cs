using Glamourer.Api.Enums;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Runtime.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary> Regression tests for the review findings fixed after the first implementation pass. </summary>
public class ReviewFixTests
{
    // ---- #3: a failed section is retried on the next evaluation --------------------------------------

    [Fact]
    public void FailedGlamourerApply_LeavesDesignHashNull_AndRetriesGlamourerOnly()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var design = Designs.Of(d =>
        {
            Designs.Head(d, 1);
            Designs.Mod(d, "ModA");
        });

        rig.Glamourer.ApplyResult = GlamourerApiEc.ActorNotFound;
        var first = rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());

        Assert.Contains(first.Problems, p => p.Contains("ApplyState failed"));
        Assert.Null(rig.Store.DesignHash);
        Assert.True(rig.Store.ModsApplied);
        Assert.False(rig.Store.GlamourerApplied);

        rig.Glamourer.ApplyResult = GlamourerApiEc.Success;
        rig.Penumbra.ClearCalls();
        var delta = DesignDiffer.Diff(rig.Store, design, false);
        Assert.False(delta.NoChange);
        Assert.True(delta.GlamourerChanged);
        Assert.False(delta.ModsChanged);

        var second = rig.Applier.Apply(design, delta, Designs.Safe());
        Assert.True(second.GlamourerApplied);
        Assert.Empty(rig.Penumbra.CallsNamed("RemoveAllTemporaryModSettings"));
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
    }

    [Fact]
    public void MissingMod_IsNotTransient_DesignHashIsStored()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Mod(d, "NotInstalled"));

        var info = rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());

        Assert.Contains(info.Problems, p => p.StartsWith("mod missing"));
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
        Assert.True(DesignDiffer.Diff(rig.Store, design, false).NoChange);
    }

    // ---- #12: presets are tracked and removed ------------------------------------------------------

    [Fact]
    public void PresetDroppedFromDesign_IsRemovedFromActor()
    {
        var rig    = new EngineRig();
        var a      = rig.Moodles.AddMoodle("A");
        var preset = rig.Moodles.AddPreset("P", a);
        var with   = Designs.Of(d => Designs.Moodle(d, preset, MoodleOp.AddPreset));
        var without = Designs.Of(d => Designs.Head(d, 1));

        rig.Applier.Apply(with, DesignDiffer.Diff(rig.Store, with, false), Designs.Safe());
        Assert.Contains(preset, rig.Store.PresetsApplied);
        Assert.Contains(a, rig.Moodles.ActiveOn(rig.PlayerAddress));

        var delta = DesignDiffer.Diff(rig.Store, without, false);
        Assert.Contains(preset, delta.PresetRemoves);
        Assert.True(delta.MoodlesChanged);

        rig.Applier.Apply(without, delta, Designs.Safe());
        Assert.Empty(rig.Store.PresetsApplied);
        Assert.Single(rig.Moodles.CallsNamed("RemovePreset"));
        Assert.DoesNotContain(a, rig.Moodles.ActiveOn(rig.PlayerAddress));
    }

    [Fact]
    public void MoodlesCalls_StampTheStore()
    {
        var rig    = new EngineRig();
        var a      = rig.Moodles.AddMoodle("A");
        var design = Designs.Of(d => Designs.Moodle(d, a));

        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());

        Assert.Equal(rig.Now, rig.Store.LastMoodlesCallUtc);
    }

    // ---- #10: the design cache notices edits in Glamourer ------------------------------------------

    [Fact]
    public void RefreshNames_DropsRenamedAndDeletedDesigns()
    {
        var bridge = new Compose.FakeGlamourerBridge();
        var cache  = new GlamourerDesignCache(bridge);
        var kept    = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        foreach (var id in new[] { kept, renamed, deleted })
        {
            bridge.Designs[id] = new JObject { ["FileVersion"] = 3, ["Equipment"] = new JObject() };
            bridge.Names[id]   = id.ToString("N")[..6];
        }

        _ = cache.Names;
        Assert.NotNull(cache.GetRaw(kept));
        Assert.NotNull(cache.GetRaw(renamed));
        Assert.NotNull(cache.GetRaw(deleted));
        var fetches = bridge.GetDesignCalls;

        Assert.False(cache.RefreshNames());
        Assert.Equal(fetches, bridge.GetDesignCalls);

        bridge.Names[renamed] = "New Name";
        bridge.Names.Remove(deleted);
        bridge.Designs.Remove(deleted);

        Assert.True(cache.RefreshNames());
        Assert.NotNull(cache.GetRaw(kept));
        Assert.Equal(fetches, bridge.GetDesignCalls);
        Assert.NotNull(cache.GetRaw(renamed));
        Assert.Equal(fetches + 1, bridge.GetDesignCalls);
        Assert.Null(cache.GetRaw(deleted));
        Assert.Equal("New Name", cache.TryGetDesignName(renamed));
    }

    // ---- #15: profile switch keeps the Glamourer state in place ------------------------------------

    [Fact]
    public void ProfileSwitch_ToDesignWithoutGlamourer_RevertsThroughTheDiff()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var a = new Profile { Name = "A", Enabled = true };
        var b = new Profile { Name = "B", Enabled = true };
        var designId = Guid.NewGuid();
        rig.ComposeGlamourer.Designs[designId] = Compose.Build.Design(d => d["Equipment"]!["Head"] = Compose.Build.Slot(3));
        rig.ComposeGlamourer.Names[designId]   = "D";
        a.PassGroups.Add(Compose.Build.Group("G", true, Compose.Build.Pass("P", true, new GlamourerLayer { DesignId = designId })));
        b.PassGroups.Add(Compose.Build.Group("G", true, Compose.Build.Pass("P", true, new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "ModA" } } })));

        rig.Core.Step(Designs.Safe(), a, false);
        Assert.True(rig.Store.GlamourerApplied);
        rig.Glamourer.ClearCalls();

        var step = rig.Core.Step(Designs.Safe(), b, false);

        Assert.True(step.ProfileSwitched);
        Assert.Equal(new[] { "UnlockAndRevertToGame" }, rig.Glamourer.Calls.Select(c => c.Name));
        Assert.False(rig.Store.GlamourerApplied);
        Assert.True(rig.Store.ModsApplied);
    }

    // ---- #13: the unlock capability is reachable through the fake -----------------------------------

    [Fact]
    public void Unlock_KeepsStateDropsLock()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 1));
        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, false), Designs.Safe());
        Assert.True(rig.Glamourer.Applied[0].Locked);

        IGlamourerUnlock unlock = rig.Glamourer;
        Assert.Equal(GlamourerApiEc.Success, unlock.Unlock(0));

        Assert.False(rig.Glamourer.Applied[0].Locked);
        Assert.Equal(1, rig.Glamourer.Applied[0].State["Equipment"]!["Head"]!["ItemId"]!.Value<long>());
    }
}
