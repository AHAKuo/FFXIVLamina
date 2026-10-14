using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.State;
using Xunit;

namespace Lamina.Tests.State;

public class ConditionEvaluatorTests
{
    /// <summary> A leaf whose raw result is set directly by the test. </summary>
    private sealed class FlagCondition : Condition
    {
        public bool Value;
        public override string Kind => "TestFlag";
        public override bool Matches(GameStateSnapshot s) => Value;
        public override string Describe() => "flag";
    }

    /// <summary> Same as FlagCondition but a transient kind: config defaults apply. </summary>
    private sealed class TransientFlagCondition : Condition
    {
        public bool Value;
        public override string Kind => "TestTransientFlag";
        public override bool IsTransient => true;
        public override bool Matches(GameStateSnapshot s) => Value;
        public override string Describe() => "transient flag";
    }

    private sealed class Harness
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public LaminaConfig Config = new() { DefaultHoldSeconds = 0, DefaultLingerSeconds = 0 };
        public ConditionEvaluator Evaluator;
        public long Sequence;

        public Harness()
            => Evaluator = new ConditionEvaluator(() => Now, () => Config);

        public GameStateSnapshot Snapshot()
            => new() { Sequence = ++Sequence };

        public void Advance(double seconds)
            => Now = Now.AddSeconds(seconds);
    }

    private static ConditionStack Stack(params ConditionNode[] nodes)
    {
        if (nodes.Length == 1 && nodes[0] is ConditionGroup g)
            return new ConditionStack { Root = g };
        var group = new ConditionGroup();
        group.Children.AddRange(nodes);
        return new ConditionStack { Root = group };
    }

    [Fact]
    public void EmptyStack_IsTrue()
    {
        var h = new Harness();
        Assert.True(h.Evaluator.Evaluate(new ConditionStack(), h.Snapshot()));
        Assert.True(h.Evaluator.Evaluate(new ConditionStack { Root = new ConditionGroup() }, h.Snapshot()));
    }

    [Fact]
    public void NoTimers_IsInstantaneous()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true };
        var stack = Stack(leaf);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void Hold_BecomesTrueOnlyAfterHoldElapsed()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 2 };
        var stack = Stack(leaf);

        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.Equal(TimeSpan.FromSeconds(2), h.Evaluator.TimeUntilNextChange);
        h.Advance(1);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.Equal(TimeSpan.FromSeconds(1), h.Evaluator.TimeUntilNextChange);
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.Null(h.Evaluator.TimeUntilNextChange);
    }

    [Fact]
    public void Hold_ResetsWhenRawFlipsEarly()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 2 };
        var stack = Stack(leaf);

        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(1.5);
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(0.1);
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(1.5);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(0.6);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void Linger_StaysTrueAfterRawTurnsFalse()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true, LingerSeconds = 3 };
        var stack = Stack(leaf);

        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.Equal(TimeSpan.FromSeconds(3), h.Evaluator.TimeUntilNextChange);
        h.Advance(2.9);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(0.2);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.Null(h.Evaluator.TimeUntilNextChange);
    }

    [Fact]
    public void Linger_RawReturningTrueCancelsExpiry()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true, LingerSeconds = 3 };
        var stack = Stack(leaf);

        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(2);
        leaf.Value = true;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(5);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void HoldAndLinger_Combined()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 1, LingerSeconds = 2 };
        var stack = Stack(leaf);

        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        h.Advance(0.5);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));   // lingering
        leaf.Value = true;
        h.Advance(0.5);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));   // hold pending but linger carries it
        h.Advance(1.5);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));   // hold met
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));   // linger starts at the observed flip
        h.Advance(2.5);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));  // linger expired
    }

    [Fact]
    public void DefaultsComeFromConfig_ForTransientKinds()
    {
        var h = new Harness();
        h.Config.DefaultHoldSeconds   = 1;
        h.Config.DefaultLingerSeconds = 2;
        var leaf = new TransientFlagCondition { Value = false };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));          // default hold pending
        Assert.Equal(TimeSpan.FromSeconds(1), h.Evaluator.TimeUntilNextChange);
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));           // default linger
        h.Advance(2.1);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void StableKinds_IgnoreConfigDefaults()
    {
        var h = new Harness();
        h.Config.DefaultHoldSeconds   = 1;
        h.Config.DefaultLingerSeconds = 2;
        var leaf = new FlagCondition { Value = false };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));           // no hold
        Assert.Null(h.Evaluator.TimeUntilNextChange);
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));          // no linger
        Assert.Null(h.Evaluator.TimeUntilNextChange);
    }

    [Fact]
    public void ExplicitValues_WinOnStableKinds()
    {
        var h = new Harness();
        h.Config.DefaultHoldSeconds = 0;
        var leaf = new FlagCondition { Value = false, HoldSeconds = 1, LingerSeconds = 1 };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));          // explicit hold applies
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));           // explicit linger applies
        h.Advance(1.1);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void ExplicitZero_OverridesTransientDefaults()
    {
        var h = new Harness();
        h.Config.DefaultHoldSeconds   = 1;
        h.Config.DefaultLingerSeconds = 2;
        var leaf = new TransientFlagCondition { Value = false, HoldSeconds = 0, LingerSeconds = 0 };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void FreshLeaf_RawTrue_IsTrueImmediately()
    {
        var h = new Harness();
        h.Config.DefaultHoldSeconds = 5;
        var explicitHold = new FlagCondition { Value = true, HoldSeconds = 5 };
        var transient    = new TransientFlagCondition { Value = true };
        Assert.True(h.Evaluator.Evaluate(Stack(explicitHold), h.Snapshot()));
        Assert.True(h.Evaluator.Evaluate(Stack(transient), h.Snapshot()));
        Assert.Null(h.Evaluator.TimeUntilNextChange);

        // Once seen, a flip false → true goes through the hold again.
        explicitHold.Value = false;
        Assert.False(h.Evaluator.Evaluate(Stack(explicitHold), h.Snapshot()));
        explicitHold.Value = true;
        Assert.False(h.Evaluator.Evaluate(Stack(explicitHold), h.Snapshot()));
        h.Advance(5);
        Assert.True(h.Evaluator.Evaluate(Stack(explicitHold), h.Snapshot()));
    }

    [Fact]
    public void StaleLeaf_SkippedSnapshots_DropsStaleLinger()
    {
        // Reviewer repro: InCombat true once, not evaluated for 10 minutes (its group lost the switch),
        // re-evaluated out of combat → must be false, not lingering.
        var h = new Harness();
        var inCombat = new TransientFlagCondition { Value = true, LingerSeconds = 600 };
        var other    = new FlagCondition { Value = true };
        Assert.True(h.Evaluator.Evaluate(Stack(inCombat), h.Snapshot()));

        for (var i = 0; i < 3; i++)
        {
            h.Advance(200);
            Assert.True(h.Evaluator.Evaluate(Stack(other), h.Snapshot()));   // inCombat skipped
        }

        inCombat.Value = false;
        Assert.False(h.Evaluator.Evaluate(Stack(inCombat), h.Snapshot()));
        Assert.Null(h.Evaluator.TimeUntilNextChange);
    }

    [Fact]
    public void StaleLeaf_SkippedSnapshots_DropsPendingHold()
    {
        var h = new Harness();
        var leaf  = new FlagCondition { Value = false, HoldSeconds = 2 };
        var other = new FlagCondition { Value = true };
        Assert.False(h.Evaluator.Evaluate(Stack(leaf), h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(Stack(leaf), h.Snapshot()));      // hold pending
        h.Advance(0.5);
        Assert.True(h.Evaluator.Evaluate(Stack(other), h.Snapshot()));      // leaf skipped
        h.Advance(0.5);
        Assert.True(h.Evaluator.Evaluate(Stack(leaf), h.Snapshot()));       // fresh again: raw true → true, no hold
    }

    [Fact]
    public void ConsecutiveSnapshots_KeepTimers()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 2 };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        h.Advance(1);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));            // evaluated every snapshot: hold continues
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void Cache_HitWithFixedClock()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true };
        var stack = Stack(leaf);
        var snap = h.Snapshot();
        Assert.True(h.Evaluator.Evaluate(stack, snap));
        leaf.Value = false;
        for (var i = 0; i < 3; i++)
            Assert.True(h.Evaluator.Evaluate(stack, snap));   // same sequence and clock reading → cached result
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void Negate_AppliesAfterTimers()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true, LingerSeconds = 2, Negate = true };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));   // still lingering → negated false
        h.Advance(2.1);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void DisabledLeaves_AreNeutral()
    {
        var h = new Harness();
        var on = new FlagCondition { Value = true };
        var offDisabled = new FlagCondition { Value = false, Enabled = false };
        Assert.True(h.Evaluator.Evaluate(Stack(on, offDisabled), h.Snapshot()));
        Assert.True(h.Evaluator.Evaluate(Stack(offDisabled), h.Snapshot()));
        var any = new ConditionGroup { Op = GroupOp.Any };
        any.Children.Add(offDisabled);
        Assert.True(h.Evaluator.Evaluate(Stack(any), h.Snapshot()));
    }

    [Fact]
    public void Groups_AllAnyNegate()
    {
        var h = new Harness();
        var t = new FlagCondition { Value = true };
        var f = new FlagCondition { Value = false };

        var all = new ConditionGroup { Op = GroupOp.All };
        all.Children.AddRange([t, f]);
        Assert.False(h.Evaluator.Evaluate(Stack(all), h.Snapshot()));

        var any = new ConditionGroup { Op = GroupOp.Any };
        any.Children.AddRange([t, f]);
        Assert.True(h.Evaluator.Evaluate(Stack(any), h.Snapshot()));

        var negated = new ConditionGroup { Op = GroupOp.Any, Negate = true };
        negated.Children.AddRange([t, f]);
        Assert.False(h.Evaluator.Evaluate(Stack(negated), h.Snapshot()));

        var nested = new ConditionGroup { Op = GroupOp.All };
        nested.Children.Add(t);
        nested.Children.Add(negated);
        Assert.False(h.Evaluator.Evaluate(Stack(nested), h.Snapshot()));
    }

    [Fact]
    public void Cache_PerSequenceAndClock()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = true };
        var stack = Stack(leaf);
        var snap = h.Snapshot();
        Assert.True(h.Evaluator.Evaluate(stack, snap));
        leaf.Value = false;
        Assert.True(h.Evaluator.Evaluate(stack, snap));     // same sequence, same clock → cached
        h.Advance(0.1);
        Assert.False(h.Evaluator.Evaluate(stack, snap));    // same snapshot, clock moved → re-evaluated
        leaf.Value = true;
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void EvaluateInstant_IgnoresTimers()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 5 };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.True(h.Evaluator.EvaluateInstant(stack, h.Snapshot()));
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
    }

    [Fact]
    public void Reset_ClearsTimers()
    {
        var h = new Harness();
        var leaf = new FlagCondition { Value = false, HoldSeconds = 1 };
        var stack = Stack(leaf);
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        Assert.NotNull(h.Evaluator.TimeUntilNextChange);
        h.Evaluator.Reset();
        Assert.Null(h.Evaluator.TimeUntilNextChange);
        Assert.True(h.Evaluator.Evaluate(stack, h.Snapshot()));            // fresh after reset: raw true → true
        leaf.Value = false;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));
        leaf.Value = true;
        Assert.False(h.Evaluator.Evaluate(stack, h.Snapshot()));           // hold pending again
    }

    [Fact]
    public void SharedIds_ShareOneTimer()
    {
        var h = new Harness();
        var id = Guid.NewGuid();
        var a = new FlagCondition { Value = false, HoldSeconds = 1, Id = id };
        var b = new FlagCondition { Value = true, HoldSeconds = 1, Id = id };
        Assert.False(h.Evaluator.Evaluate(Stack(a), h.Snapshot()));
        a.Value = true;
        Assert.False(h.Evaluator.Evaluate(Stack(a), h.Snapshot()));        // hold started on the shared id
        h.Advance(1);
        Assert.True(h.Evaluator.Evaluate(Stack(b), h.Snapshot()));         // b continues a's timer
    }

    [Fact]
    public void ThrowingCondition_CountsAsFalse()
    {
        var h = new Harness();
        var leaf = new ThrowingCondition();
        Assert.False(h.Evaluator.Evaluate(Stack(leaf), h.Snapshot()));
        Assert.False(h.Evaluator.EvaluateInstant(Stack(leaf), h.Snapshot()));
    }

    private sealed class ThrowingCondition : Condition
    {
        public override string Kind => "Throwing";
        public override bool Matches(GameStateSnapshot s) => throw new InvalidOperationException();
        public override string Describe() => "throws";
    }
}
