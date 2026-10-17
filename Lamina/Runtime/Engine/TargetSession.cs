using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime.Compose;
using Lamina.Runtime.State;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Everything the engine keeps per target: its own applied state, redraw scheduler, applier, cleanup and core, plus where the
/// character is in the object table right now. The local player is a session like any other; the others follow a character by
/// name while it is rendered. Dalamud-free apart from the locate callback the engine injects.
/// </summary>
public sealed class TargetSession
{
    private readonly Func<DateTime, ResolvedActor?> _locate;
    private readonly Func<DateTime> _clock;

    /// <param name="target"> Who this session applies to. </param>
    /// <param name="locate"> Finds the character in the object table right now (validated on every call), null while absent. </param>
    /// <param name="glamourer"> Glamourer bridge. </param>
    /// <param name="penumbra"> Penumbra bridge. </param>
    /// <param name="honorific"> Honorific bridge. </param>
    /// <param name="moodles"> Moodles bridge. </param>
    /// <param name="customizePlus"> Customize+ bridge. </param>
    /// <param name="heels"> Simple Heels bridge. </param>
    /// <param name="decals"> Decal publisher; only the local player gets one (the bake is projected onto your own body meshes). </param>
    /// <param name="evaluator"> Condition evaluator for compose; the cleanup of a non-local session never resets its timers. </param>
    /// <param name="compositor"> Shared pass compositor. </param>
    /// <param name="resolver"> Shared layer resolver. </param>
    /// <param name="config"> Current config. </param>
    /// <param name="clock"> UTC clock. </param>
    /// <param name="log"> Debug logger. </param>
    /// <param name="drawObjectKey"> Identity of the local player's draw object; null for other targets. </param>
    public TargetSession(Target target, Func<DateTime, ResolvedActor?> locate, IGlamourerBridge glamourer, IPenumbraBridge penumbra,
        IHonorificBridge honorific, IMoodlesBridge moodles, ICustomizePlusBridge customizePlus, ISimpleHeelsBridge heels,
        IDecalPublisher? decals, IConditionEvaluator evaluator, PassCompositor compositor, LayerResolver resolver,
        Func<LaminaConfig> config, Func<DateTime> clock, Action<string> log, Func<string?>? drawObjectKey = null)
    {
        Target  = target;
        _locate = locate;
        _clock  = clock;

        var cleanupEvaluator = target.IsLocal ? evaluator : new NoResetEvaluator(evaluator);
        Redraw  = new RedrawScheduler(penumbra, Index, log);
        Applier = new Applier(glamourer, penumbra, honorific, moodles, customizePlus, heels, target.IsLocal ? decals : null, Store, Redraw, config,
            () => CollectionId, () => CollectionStatus, Index, Address, clock, log, drawObjectKey, writeModsWhenUnassigned: !target.IsLocal);
        Cleanup = new CleanupService(glamourer, penumbra, honorific, moodles, customizePlus, heels, target.IsLocal ? decals : null, Store, Redraw,
            cleanupEvaluator, config, () => CollectionId, Index, Address, clock, m => log($"[{target}] {m}"));
        Core = new EngineCore(compositor, resolver, evaluator, Applier, Cleanup, Store, config, target.Kind, retainRandom: false);
    }

    public Target Target { get; }

    public AppliedStateStore Store { get; } = new();

    public RedrawScheduler Redraw { get; }

    public Applier Applier { get; }

    public CleanupService Cleanup { get; }

    public EngineCore Core { get; }

    /// <summary> The Penumbra collection this target's mods go into, when found. </summary>
    public Guid? CollectionId { get; set; }

    /// <summary> Where this target stands with its collection. </summary>
    public CollectionStatus CollectionStatus { get; set; } = CollectionStatus.PenumbraMissing;

    /// <summary> The collection the target was last seen assigned to, for the opt-out cleanup. </summary>
    public Guid? LastAssignedCollection { get; set; }

    /// <summary> Where the character was last located; refreshed by <see cref="Refresh"/>. </summary>
    public ResolvedActor? Actor { get; private set; }

    /// <summary> True while the character is rendered. </summary>
    public bool Present
        => Actor is not null;

    /// <summary> True when the character was rendered at the previous evaluation, to notice it coming back. </summary>
    public bool WasPresent { get; set; }

    /// <summary> True once an absent-actor cleanup ran since the character was last seen, so it does not repeat every tick. </summary>
    public bool AbsentCleanupDone { get; set; }

    /// <summary> A named player target that is the character you are logged in as while a Local Player profile is active: it yields to that profile. </summary>
    public bool Shadowed { get; set; }

    /// <summary> No enabled profile targets this character any more; the session is dropped once nothing of Lamina's is left on it. </summary>
    public bool Retired { get; set; }

    /// <summary> The profile the last evaluation applied, or null. </summary>
    public Profile? Profile { get; set; }

    public VirtualDesign? LastDesign { get; set; }

    public EvaluationReport? LastReport { get; set; }

    public ApplyInfo? LastApply { get; set; }

    /// <summary> Sections the composed design carried that this target cannot take (see <see cref="TargetCapabilities"/>). </summary>
    public IReadOnlyList<string> Dropped { get; set; } = Array.Empty<string>();

    /// <summary> Re-apply every section on the next evaluation. </summary>
    public bool Force { get; set; }

    /// <summary> After an apply with problems, no retry before this time. </summary>
    public DateTime? RetryDue { get; set; }

    /// <summary> Glamourer automation wrote over Lamina's state on this character; re-assert on the next tick. </summary>
    public bool ReassertGlamourer { get; set; }

    /// <summary> Finds the character now and records it. </summary>
    public ResolvedActor? Refresh(DateTime nowUtc)
        => Actor = _locate(nowUtc);

    /// <summary> Object index of the character, validated right now, or null while absent. Also refreshes <see cref="Actor"/>. </summary>
    public int? Index()
        => Refresh(_clock())?.Index;

    /// <summary> Actor address of the character, validated right now, 0 while absent. </summary>
    public nint Address()
        => Refresh(_clock())?.Address ?? 0;

    /// <summary> Forgets the applied design hash so the next evaluation diffs every section again. </summary>
    public void InvalidateDesign()
        => Store.DesignHash = null;

    /// <summary> Forgets the Glamourer section so the normal diff re-applies it. </summary>
    public void ForgetGlamourer()
    {
        Store.GlamourerHash = null;
        Store.DesignHash    = null;
    }

    /// <summary> Forgets the mod section (and decals, which live in the same collection). </summary>
    public void ForgetMods()
    {
        Store.ForgetMods();
        Store.ForgetDecals();
        Store.DesignHash = null;
    }

    /// <summary> Forgets the last outcome after a cleanup. </summary>
    public void ForgetOutcome()
    {
        Core.ForgetProfile();
        LastDesign = null;
        LastReport = null;
        Profile    = null;
    }

    /// <summary> A view of the evaluator whose <see cref="IConditionEvaluator.Reset"/> does nothing: timers are shared by every target. </summary>
    private sealed class NoResetEvaluator : IConditionEvaluator
    {
        private readonly IConditionEvaluator _inner;

        public NoResetEvaluator(IConditionEvaluator inner)
            => _inner = inner;

        public bool Evaluate(ConditionStack stack, GameStateSnapshot snapshot)
            => _inner.Evaluate(stack, snapshot);

        public bool EvaluateInstant(ConditionStack stack, GameStateSnapshot snapshot)
            => _inner.EvaluateInstant(stack, snapshot);

        public TimeSpan? TimeUntilNextChange
            => _inner.TimeUntilNextChange;

        public void Reset()
        { }
    }
}
