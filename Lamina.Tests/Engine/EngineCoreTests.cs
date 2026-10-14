using Lamina.Models;
using Lamina.Runtime.Engine;
using Lamina.Tests.Compose;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Engine;

public class EngineCoreTests
{
    private static Profile ProfileWith(EngineRig rig, ulong headItem, string? modDir = null, bool groupMatches = true)
    {
        var design = Build.Design(d =>
        {
            d["Equipment"]!["Head"] = Build.Slot(headItem);
            if (modDir is not null)
                d["Mods"] = new JArray(new JObject { ["Name"] = modDir, ["Directory"] = modDir, ["State"] = "Enabled", ["Priority"] = 0 });
        });
        var id = Guid.NewGuid();
        rig.ComposeGlamourer.Designs[id] = design;
        rig.ComposeGlamourer.Names[id]   = "D";

        var profile = new Profile { Name = "P", Enabled = true };
        profile.PassGroups.Add(Build.Group("G", groupMatches, Build.Pass("Pass", true, new GlamourerLayer { DesignId = id })));
        return profile;
    }

    [Fact]
    public void FirstStep_Applies_SecondStep_NoChange()
    {
        var rig     = new EngineRig();
        var profile = ProfileWith(rig, 7);

        var first = rig.Core.Step(Designs.Safe(), profile, force: false);
        Assert.NotNull(first.Apply);
        Assert.True(first.Apply!.GlamourerApplied);
        Assert.Equal(profile.Id, rig.Core.LastProfileId);
        Assert.Contains(profile.PassGroups[0].Passes[0].Id, first.Report!.MatchedPassIds);

        var second = rig.Core.Step(Designs.Safe(), profile, force: false);
        Assert.True(second.Delta.NoChange);
        Assert.Null(second.Apply);
        Assert.Single(rig.Glamourer.CallsNamed("ApplyState"));
    }

    [Fact]
    public void Force_ReappliesWithoutChange()
    {
        var rig     = new EngineRig();
        var profile = ProfileWith(rig, 7);
        rig.Core.Step(Designs.Safe(), profile, false);

        var forced = rig.Core.Step(Designs.Safe(), profile, force: true);

        Assert.NotNull(forced.Apply);
        Assert.Equal(2, rig.Glamourer.CallsNamed("ApplyState").Count());
    }

    [Fact]
    public void ModsChange_RequestsOneRedraw()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var profile = ProfileWith(rig, 7, "ModA");

        var step = rig.Core.Step(Designs.Safe(), profile, false);

        Assert.True(step.Delta.RequiresRedraw);
        Assert.True(rig.Redraw.Pending);
        Assert.True(rig.Redraw.Tick(Designs.Safe(), RedrawPolicy.DeferWhenUnsafe));
        Assert.Single(rig.Penumbra.Redraws);
    }

    [Fact]
    public void ProfileSwitch_CleansUpThenApplies()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var a = ProfileWith(rig, 1, "ModA");
        var b = ProfileWith(rig, 2);
        rig.Core.Step(Designs.Safe(), a, false);
        rig.Penumbra.ClearCalls();
        rig.Glamourer.ClearCalls();

        var step = rig.Core.Step(Designs.Safe(), b, false);

        Assert.True(step.ProfileSwitched);
        // The Glamourer state is replaced in place: no revert to automation (and no one-frame flash) between profiles.
        // GetState is the read-only baseline lookup the applier does before every ApplyState.
        var calls = rig.Glamourer.Calls.Select(c => c.Name).Where(n => n != "GetState").ToList();
        Assert.Equal(new[] { "ApplyState" }, calls);
        Assert.Equal(2, rig.Glamourer.Applied[0].State["Equipment"]!["Head"]!["ItemId"]!.Value<long>());
        Assert.Empty(rig.Penumbra.TempSettingsIn(rig.Collection));
        Assert.Equal(b.Id, rig.Core.LastProfileId);
    }

    [Fact]
    public void NoProfile_RevertsWhatWasApplied()
    {
        var rig     = new EngineRig();
        var profile = ProfileWith(rig, 1);
        rig.Core.Step(Designs.Safe(), profile, false);

        var step = rig.Core.Step(Designs.Safe(), null, false);

        Assert.Null(step.Profile);
        Assert.True(step.Design.IsEmpty);
        Assert.False(rig.Store.GlamourerApplied);
        Assert.Contains(rig.Glamourer.Calls, c => c.Name == "UnlockAndRevertToAutomation");
    }

    [Fact]
    public void NoGroupMatches_EmptyDesign_PassesThrough()
    {
        var rig     = new EngineRig();
        var profile = ProfileWith(rig, 1, groupMatches: false);

        var step = rig.Core.Step(Designs.Safe(), profile, false);

        Assert.True(step.Design.IsEmpty);
        Assert.Null(step.Report!.ActiveGroupId);
        Assert.Empty(rig.Glamourer.Calls);
    }
}
