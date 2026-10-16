using Glamourer.Api.Enums;
using Lamina.Models;
using Lamina.Persistence;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary>
/// Fall back to the game state when no rule applies: a Glamourer field no rule sets any more is reverted (to the game state by
/// default, to automation when configured) before the next design is applied in the same frame, and the full cleanup
/// reverts the same way.
/// </summary>
public class NoRuleFallbackTests
{
    private static DesignDelta Diff(VirtualDesign design, AppliedStateStore store)
        => DesignDiffer.Diff(store, design, force: false);

    private static void Hair(DesignDraft d, int value)
        => d.Customize[CustomizeIndex.Hairstyle] = new Field<int>(value, Designs.Src);

    /// <summary> Glamourer calls made, GetState left out since it is a read. </summary>
    private static List<string> Writes(EngineRig rig)
        => rig.Glamourer.Calls.Select(c => c.Name).Where(n => n != "GetState").ToList();

    [Fact]
    public void Config_DefaultsToGameState_AndRoundTrips()
    {
        Assert.Equal(NoRuleFallback.GameState, new LaminaConfig().NoRuleFallback);

        // Older config files have no such property: the default applies.
        var old = JsonSettings.Deserialize<LaminaConfig>("""{ "version": 1, "enabled": true }""");
        Assert.Equal(NoRuleFallback.GameState, old.NoRuleFallback);

        var json = JsonSettings.Serialize(new LaminaConfig { NoRuleFallback = NoRuleFallback.Automation });
        Assert.Contains("\"noRuleFallback\": \"Automation\"", json);
        Assert.Equal(NoRuleFallback.Automation, JsonSettings.Deserialize<LaminaConfig>(json).NoRuleFallback);
    }

    [Fact]
    public void EmptyDesign_AfterApplied_RevertsToGameState_ByDefault()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(design, Diff(design, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var empty = Designs.Empty();
        var info  = rig.Applier.Apply(empty, Diff(empty, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame" }, Writes(rig));
        Assert.True(info.GlamourerApplied);
        Assert.Empty(info.Problems);
        Assert.False(rig.Store.GlamourerApplied);
        Assert.Null(rig.Store.LastGlamourerDesign);
    }

    [Fact]
    public void EmptyDesign_AfterApplied_RevertsToAutomation_WhenConfigured()
    {
        var rig = new EngineRig();
        rig.Config.NoRuleFallback = NoRuleFallback.Automation;
        var design = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(design, Diff(design, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var empty = Designs.Empty();
        rig.Applier.Apply(empty, Diff(empty, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToAutomation" }, Writes(rig));
        Assert.False(rig.Store.GlamourerApplied);
    }

    [Fact]
    public void DroppedCustomizeField_RevertsThenApplies_InOneCall()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());
        Assert.Same(first, rig.Store.LastGlamourerDesign);
        rig.Glamourer.ClearCalls();

        // The hair rule stopped matching: Head stays, Hairstyle is no longer set by anything.
        var second = Designs.Of(d => Designs.Head(d, 42));
        var info   = rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame", "ApplyState" }, Writes(rig));
        Assert.True(info.GlamourerApplied);
        Assert.Empty(info.Problems);
        Assert.Same(second, rig.Store.LastGlamourerDesign);
        Assert.True(rig.Store.GlamourerApplied);
        // The kept field is re-asserted and the lock re-established by the ApplyState that follows the revert.
        var (state, locked) = rig.Glamourer.Applied[0];
        Assert.True(locked);
        Assert.Equal(42, state["Equipment"]!["Head"]!["ItemId"]!.Value<long>());
        Assert.Contains(rig.Log, l => l.Contains("Customize.Hairstyle") && l.Contains("GameState"));
    }

    [Fact]
    public void DroppedField_RevertsToAutomation_WhenConfigured()
    {
        var rig = new EngineRig();
        rig.Config.NoRuleFallback = NoRuleFallback.Automation;
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var second = Designs.Of(d => Hair(d, 5));
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToAutomation", "ApplyState" }, Writes(rig));
        Assert.Contains(rig.Log, l => l.Contains("Item.Head") && l.Contains("Automation"));
    }

    [Fact]
    public void ChangedValue_SameKeys_AppliesWithoutRevert()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var second = Designs.Of(d => { Designs.Head(d, 43); Hair(d, 6); });
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "ApplyState" }, Writes(rig));
        Assert.Same(second, rig.Store.LastGlamourerDesign);
    }

    [Fact]
    public void AddedField_AppliesWithoutRevert()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var second = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "ApplyState" }, Writes(rig));
    }

    [Fact]
    public void ForcedReapply_OfTheSameDesign_DoesNotRevert()
    {
        var rig    = new EngineRig();
        var design = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(design, Diff(design, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        // What the engine does after automation wrote over Lamina's state (re-assert) or on /lam reapply.
        rig.Store.GlamourerHash = null;
        rig.Store.DesignHash    = null;
        rig.Applier.Apply(design, DesignDiffer.Diff(rig.Store, design, force: true), Designs.Safe());

        Assert.Equal(new[] { "ApplyState" }, Writes(rig));
    }

    [Fact]
    public void FailedApplyState_DoesNotRecordTheDesign()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());

        rig.Glamourer.ApplyResult = GlamourerApiEc.ActorNotFound;
        var second = Designs.Of(d => { Designs.Head(d, 43); Hair(d, 5); });
        var info   = rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.False(info.GlamourerApplied);
        Assert.Same(first, rig.Store.LastGlamourerDesign);

        // Once the actor is back, dropping a field is still measured against what was really applied (first).
        rig.Glamourer.ApplyResult = GlamourerApiEc.Success;
        rig.Glamourer.ClearCalls();
        var third = Designs.Of(d => Designs.Head(d, 43));
        rig.Applier.Apply(third, Diff(third, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame", "ApplyState" }, Writes(rig));
        Assert.Same(third, rig.Store.LastGlamourerDesign);
    }

    [Fact]
    public void FailedApplyState_AfterARevert_ForgetsTheOldDesign_SoTheRetryDoesNotRevertAgain()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());

        rig.Glamourer.ApplyResult = GlamourerApiEc.ActorNotFound;
        var second = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());
        Assert.False(rig.Store.GlamourerApplied);
        Assert.Null(rig.Store.LastGlamourerDesign);

        rig.Glamourer.ApplyResult = GlamourerApiEc.Success;
        rig.Glamourer.ClearCalls();
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "ApplyState" }, Writes(rig));
        Assert.Same(second, rig.Store.LastGlamourerDesign);
    }

    [Fact]
    public void FailedRevert_SkipsApply_AndKeepsTheOldDesign_ForRetry()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        rig.Glamourer.RevertToGameResult = GlamourerApiEc.ActorNotFound;
        var second = Designs.Of(d => Designs.Head(d, 42));
        var info   = rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame" }, Writes(rig));
        Assert.False(info.GlamourerApplied);
        Assert.Contains(info.Problems, p => p.Contains("revert failed"));
        Assert.Null(rig.Store.DesignHash);
        Assert.Same(first, rig.Store.LastGlamourerDesign);

        rig.Glamourer.RevertToGameResult = GlamourerApiEc.Success;
        rig.Glamourer.ClearCalls();
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame", "ApplyState" }, Writes(rig));
    }

    [Fact]
    public void ProfileSwitch_DroppedField_StillFallsBack()
    {
        var rig   = new EngineRig();
        var first = Designs.Of(d => { Designs.Head(d, 42); Hair(d, 5); });
        rig.Applier.Apply(first, Diff(first, rig.Store), Designs.Safe());

        rig.Cleanup.CleanupForProfileSwitch();
        Assert.Same(first, rig.Store.LastGlamourerDesign);
        rig.Glamourer.ClearCalls();

        var second = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(second, Diff(second, rig.Store), Designs.Safe());

        Assert.Equal(new[] { "UnlockAndRevertToGame", "ApplyState" }, Writes(rig));
    }

    [Theory]
    [InlineData(NoRuleFallback.GameState, "UnlockAndRevertToGame")]
    [InlineData(NoRuleFallback.Automation, "UnlockAndRevertToAutomation")]
    public void Cleanup_UsesTheConfiguredRevert(NoRuleFallback fallback, string expectedCall)
    {
        var rig = new EngineRig();
        rig.Config.NoRuleFallback = fallback;
        var design = Designs.Of(d => Designs.Head(d, 42));
        rig.Applier.Apply(design, Diff(design, rig.Store), Designs.Safe());
        rig.Glamourer.ClearCalls();

        var result = rig.Cleanup.Cleanup("test", redrawNow: true);

        Assert.True(result.GlamourerReverted);
        Assert.Equal(new[] { expectedCall }, Writes(rig));
        Assert.Null(rig.Store.LastGlamourerDesign);
        Assert.False(rig.Store.GlamourerApplied);
    }

    [Fact]
    public void DroppedGlamourerFields_NamesEverySection()
    {
        var before = Designs.Of(d =>
        {
            Designs.Head(d, 1);
            d.Stains[EquipSlot.Body]        = new Field<Stains>(new Stains(1, 2), Designs.Src);
            d.Crests[EquipSlot.Head]        = new Field<bool>(true, Designs.Src);
            d.Bonus[BonusSlot.Glasses]      = new Field<ulong>(7, Designs.Src);
            Hair(d, 5);
            d.ModelId                       = new Field<uint>(3, Designs.Src);
            d.Meta[MetaIndex.HatVisible]    = new Field<bool>(false, Designs.Src);
            d.Parameters["FacePaintUvMultiplier"] = new Field<JObject>(new JObject(), Designs.Src);
            d.Materials["m1"]               = new Field<JToken>(new JObject(), Designs.Src);
        });
        var after = Designs.Of(d => Designs.Head(d, 2));

        var dropped = DesignDiffer.DroppedGlamourerFields(before, after);

        Assert.Equal(new[]
        {
            "Stain.Body", "Crest.Head", "Bonus.Glasses", "Customize.Hairstyle", "ModelId", "Meta.HatVisible",
            "Parameter.FacePaintUvMultiplier", "Material.m1",
        }, dropped);
        Assert.Empty(DesignDiffer.DroppedGlamourerFields(after, before));
    }
}
