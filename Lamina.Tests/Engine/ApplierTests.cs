using Glamourer.Api.Enums;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Engine;

public class ApplierTests
{
    private static DesignDelta Full(VirtualDesign design, AppliedStateStore store)
        => DesignDiffer.Diff(store, design, force: false);

    [Fact]
    public void Penumbra_CleanSlateBeforeWrites_WithKeyAndSource()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        rig.Penumbra.AddMod("ModB");
        var design = Designs.Of(d =>
        {
            Designs.Mod(d, "ModA", priority: 3, settings: new() { ["Group"] = new() { "Opt" } });
            Designs.Mod(d, "ModB", enabled: false);
        });

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var calls = rig.PenumbraCalls;
        Assert.Equal("RemoveAllTemporaryModSettings", calls[0]);
        Assert.Equal(2, calls.Count(c => c == "SetTemporaryModSettings"));
        Assert.True(calls.IndexOf("RemoveAllTemporaryModSettings") < calls.IndexOf("SetTemporaryModSettings"));

        var written = rig.Penumbra.TempSettingsIn(rig.Collection);
        Assert.True(written["ModA"].Enabled);
        Assert.Equal(3, written["ModA"].Priority);
        Assert.Equal(new[] { "Opt" }, written["ModA"].Settings["Group"]);
        Assert.False(written["ModB"].Enabled);

        Assert.True(info.ModsApplied);
        Assert.Equal(2, info.ModsWritten);
        Assert.True(info.RedrawRequested);
        Assert.True(rig.Redraw.Pending);
        Assert.Empty(info.Problems);
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
        Assert.True(rig.Store.ModsApplied);
    }

    [Fact]
    public void Penumbra_MissingMod_ReportedNotThrown()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Present");
        var design = Designs.Of(d => { Designs.Mod(d, "Present"); Designs.Mod(d, "Gone"); });

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.Equal(1, info.ModsWritten);
        Assert.Contains(info.Problems, p => p.Contains("Gone"));
    }

    [Fact]
    public void Glamourer_AppliedOnce_WithLock_WhenHoldEnabled()
    {
        var rig = new EngineRig();
        rig.Config.HoldGlamourerState = true;
        var design = Designs.Of(d => Designs.Head(d, 42));

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.Single(rig.Glamourer.CallsNamed("ApplyState"));
        var (state, locked) = rig.Glamourer.Applied[0];
        Assert.True(locked);
        Assert.Equal(42, state["Equipment"]!["Head"]!["ItemId"]!.Value<long>());
        Assert.True(state["Equipment"]!["Head"]!["Apply"]!.Value<bool>());
        Assert.True(info.GlamourerApplied);
        Assert.False(info.RedrawRequested);
        Assert.True(rig.Store.GlamourerApplied);
    }

    [Fact]
    public void Glamourer_NotLocked_WhenHoldDisabled()
    {
        var rig = new EngineRig();
        rig.Config.HoldGlamourerState = false;
        var design = Designs.Of(d => Designs.Head(d, 42));

        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.False(rig.Glamourer.Applied[0].Locked);
    }

    [Fact]
    public void Glamourer_RevertsToGameState_WhenDesignLosesContent()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var empty = Designs.Empty();
        var info  = rig.Applier.Apply(empty, Full(empty, rig.Store), Designs.Safe());

        // Default fallback: the plain game state, never automation.
        Assert.Single(rig.Glamourer.CallsNamed("UnlockAndRevertToGame"));
        Assert.Empty(rig.Glamourer.CallsNamed("UnlockAndRevertToAutomation"));
        Assert.True(info.GlamourerApplied);
        Assert.False(rig.Store.GlamourerApplied);
        Assert.Null(rig.Store.LastGlamourerDesign);
    }

    [Fact]
    public void Glamourer_Failure_IsAProblem()
    {
        var rig = new EngineRig();
        rig.Glamourer.ApplyResult = GlamourerApiEc.ActorNotFound;
        var design = Designs.Of(d => Designs.Head(d, 42));

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.False(info.GlamourerApplied);
        Assert.Contains(info.Problems, p => p.Contains("ActorNotFound"));
        Assert.False(rig.Store.GlamourerApplied);
    }

    [Fact]
    public void OptedOutCollection_SkipsPenumbra_StillAppliesGlamourer()
    {
        var rig = new EngineRig { Status = CollectionStatus.NotAssigned };
        rig.Penumbra.AddMod("ModA");
        var design = Designs.Of(d => { Designs.Head(d, 1); Designs.Mod(d, "ModA"); });

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.Empty(rig.Penumbra.CallsNamed("RemoveAllTemporaryModSettings"));
        Assert.Empty(rig.Penumbra.CallsNamed("SetTemporaryModSettings"));
        Assert.Single(rig.Glamourer.CallsNamed("ApplyState"));
        Assert.False(info.ModsApplied);
        Assert.False(info.RedrawRequested);
        Assert.True(info.GlamourerApplied);
    }

    [Fact]
    public void Honorific_SetAndClear_OnlyWhenAvailable()
    {
        var rig = new EngineRig();
        rig.Honorific.Available = false;
        var titled = Designs.Of(d => Designs.Title(d, "Rain Walker"));

        var info = rig.Applier.Apply(titled, Full(titled, rig.Store), Designs.Safe());
        Assert.False(info.HonorificApplied);
        Assert.Empty(rig.Honorific.Calls);
        Assert.Null(rig.Store.HonorificSet);

        rig.Honorific.Available = true;
        rig.Store.DesignHash    = null;
        info = rig.Applier.Apply(titled, Full(titled, rig.Store), Designs.Safe());
        Assert.True(info.HonorificApplied);
        Assert.Equal("Rain Walker", rig.Honorific.Titles[0].Title);
        Assert.NotNull(rig.Store.HonorificSet);

        var empty = Designs.Empty();
        info = rig.Applier.Apply(empty, Full(empty, rig.Store), Designs.Safe());
        Assert.True(info.HonorificApplied);
        Assert.False(rig.Honorific.Titles.ContainsKey(0));
        Assert.Null(rig.Store.HonorificSet);
    }

    [Fact]
    public void Moodles_RemovesBeforeAdds_TracksAdded_SkipsWhenUnavailable()
    {
        var rig = new EngineRig();
        var a   = rig.Moodles.AddMoodle("A");
        var b   = rig.Moodles.AddMoodle("B");
        var p   = rig.Moodles.AddPreset("Preset", Guid.NewGuid());

        var first = Designs.Of(d => { Designs.Moodle(d, a); Designs.Moodle(d, p, MoodleOp.AddPreset); });
        rig.Applier.Apply(first, Full(first, rig.Store), Designs.Safe());
        Assert.Contains(a, rig.Moodles.ActiveOn(rig.PlayerAddress));
        Assert.Contains(a, rig.Store.MoodlesAdded);
        Assert.Contains(p, rig.Store.PresetsApplied);

        rig.Moodles.ClearCalls();
        var second = Designs.Of(d => Designs.Moodle(d, b));
        var info   = rig.Applier.Apply(second, Full(second, rig.Store), Designs.Safe());
        var names  = rig.Moodles.Calls.Select(c => c.Name).ToList();
        // The dropped preset is removed too (review #12), between the status removes and the adds.
        Assert.Equal(new[] { "RemoveMoodles", "RemovePreset", "AddMoodle" }, names);
        Assert.Empty(rig.Store.PresetsApplied);
        Assert.DoesNotContain(a, rig.Moodles.ActiveOn(rig.PlayerAddress));
        Assert.Contains(b, rig.Moodles.ActiveOn(rig.PlayerAddress));
        Assert.True(info.MoodlesApplied);
        Assert.Equal(new HashSet<Guid> { b }, rig.Store.MoodlesAdded);

        rig.Moodles.Available = false;
        rig.Moodles.ClearCalls();
        var third = Designs.Of(d => Designs.Moodle(d, a));
        info = rig.Applier.Apply(third, Full(third, rig.Store), Designs.Safe());
        Assert.False(info.MoodlesApplied);
        Assert.Empty(rig.Moodles.Calls);
    }

    [Fact]
    public void NoChangeDelta_OnlyRefreshesHash()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 1));

        var info = rig.Applier.Apply(design, DesignDelta.None, Designs.Safe());

        Assert.Empty(rig.Glamourer.Calls);
        Assert.Empty(rig.Penumbra.Calls);
        Assert.Equal(design.ContentHash, rig.Store.DesignHash);
        Assert.False(info.GlamourerApplied);
    }

    [Fact]
    public void NoLocalPlayer_IsAProblem_NothingCalled()
    {
        var rig    = new EngineRig { PlayerIndex = null };
        var design = Designs.Of(d => Designs.Head(d, 1));

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        Assert.Contains("no local player", info.Problems);
        Assert.Empty(rig.Glamourer.Calls);
    }

    [Fact]
    public void Redraw_DeferredFlag_WhenUnsafe()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var design = Designs.Of(d => Designs.Mod(d, "ModA"));

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Unsafe());

        Assert.True(info.RedrawRequested);
        Assert.True(info.RedrawDeferred);
    }

    [Fact]
    public void StateBuilder_OmitsMods()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("ModA");
        var design = Designs.Of(d => { Designs.Head(d, 1); Designs.Mod(d, "ModA"); });

        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var state = rig.Glamourer.Applied[0].State;
        Assert.Null(state["Mods"]);
        Assert.IsType<JObject>(state["Equipment"]);
    }
}
