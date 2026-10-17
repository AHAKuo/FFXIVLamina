using Lamina.Models;
using Lamina.Runtime.Compose;

namespace Lamina.Runtime.Engine;

/// <summary> What one engine step did. </summary>
/// <param name="Profile"> The profile that was evaluated, or null when none is active. </param>
/// <param name="Design"> The composed design as applied to this target (sections the target cannot take already removed; Empty when no profile is active). </param>
/// <param name="Report"> Which groups / passes matched. </param>
/// <param name="Delta"> The computed delta. </param>
/// <param name="Apply"> The apply outcome; null when the delta was NoChange. </param>
/// <param name="ProfileSwitched"> True when a cleanup ran because the active profile changed identity. </param>
/// <param name="Dropped"> Sections the composed design carried that this target cannot take, with the reason (see <see cref="TargetCapabilities"/>). </param>
public sealed record EngineStep(
    Profile? Profile,
    VirtualDesign Design,
    EvaluationReport? Report,
    DesignDelta Delta,
    ApplyInfo? Apply,
    bool ProfileSwitched,
    IReadOnlyList<string> Dropped);

/// <summary>
/// The Dalamud-free heart of the pipeline for one target: snapshot + active profile in, compose → trim to what the target can
/// take → diff → apply out. <see cref="LaminaEngine"/> drives one core per target from the framework thread and adds the event plumbing.
/// </summary>
public sealed class EngineCore
{
    private readonly PassCompositor _compositor;
    private readonly LayerResolver _resolver;
    private readonly IConditionEvaluator _evaluator;
    private readonly Applier _applier;
    private readonly CleanupService _cleanup;
    private readonly AppliedStateStore _store;
    private readonly Func<LaminaConfig> _config;
    private readonly TargetKind _kind;
    private readonly bool _retainRandom;
    private Guid? _lastProfileId;

    /// <param name="compositor"> Pass compositor. </param>
    /// <param name="resolver"> Layer resolver. </param>
    /// <param name="evaluator"> Condition evaluator (hold / linger). </param>
    /// <param name="applier"> Applier. </param>
    /// <param name="cleanup"> Cleanup service, run on profile switches. </param>
    /// <param name="store"> Applied state. </param>
    /// <param name="config"> Current config. </param>
    /// <param name="kind"> What kind of target this core applies to; decides which sections are dropped before diffing. </param>
    /// <param name="retainRandom"> Let the compositor forget random picks of passes that stopped contributing. False when several cores share one compositor; the engine then retains the union itself. </param>
    public EngineCore(PassCompositor compositor, LayerResolver resolver, IConditionEvaluator evaluator, Applier applier,
        CleanupService cleanup, AppliedStateStore store, Func<LaminaConfig> config, TargetKind kind = TargetKind.LocalPlayer, bool retainRandom = true)
    {
        _compositor   = compositor;
        _resolver     = resolver;
        _evaluator    = evaluator;
        _applier      = applier;
        _cleanup      = cleanup;
        _store        = store;
        _config       = config;
        _kind         = kind;
        _retainRandom = retainRandom;
    }

    /// <summary> The id of the profile the last step evaluated. </summary>
    public Guid? LastProfileId
        => _lastProfileId;

    /// <summary> The target kind this core applies to. </summary>
    public TargetKind Kind
        => _kind;

    /// <summary>
    /// Evaluates <paramref name="profile"/> against <paramref name="snapshot"/>, diffs against the applied state and applies.
    /// A profile switch (different id than the last step) first clears the collection, Honorific and Moodles, keeps the
    /// Glamourer state in place, and then applies the new design: Glamourer is replaced (or reverted when the new design has
    /// no Glamourer content) by the normal diff in the same frame, without a visible flash to the automation look.
    /// With no profile the design is empty: everything passes through, and anything Lamina had applied is reverted.
    /// Sections the target cannot take are removed before diffing and listed in <see cref="EngineStep.Dropped"/>.
    /// </summary>
    /// <param name="snapshot"> Current game state. </param>
    /// <param name="profile"> The active profile for the target, or null. </param>
    /// <param name="force"> Re-apply every section even when nothing changed. </param>
    public EngineStep Step(GameStateSnapshot snapshot, Profile? profile, bool force)
    {
        var switched = false;
        if (_lastProfileId is { } last && profile?.Id != last && (_store.DesignHash is not null || _store.ModsApplied || _store.GlamourerApplied))
        {
            _cleanup.CleanupForProfileSwitch();
            switched = true;
        }

        _lastProfileId = profile?.Id;

        VirtualDesign design;
        EvaluationReport? report = null;
        var dropped = new List<string>();
        if (profile is null)
        {
            design = VirtualDesign.Empty;
        }
        else
        {
            (design, report) = _compositor.Compose(profile, snapshot, _evaluator, _config().Direction, _resolver, _retainRandom);
            design = TargetCapabilities.Trim(design, _kind, dropped);
        }

        var delta = DesignDiffer.Diff(_store, design, force);
        var apply = delta.NoChange ? null : _applier.Apply(design, delta, snapshot);
        if (delta.NoChange)
        {
            _store.DesignHash = design.ContentHash;
            _store.LastDesign = design;
        }

        return new EngineStep(profile, design, report, delta, apply, switched, dropped);
    }

    /// <summary> Forgets the last profile id (after a cleanup driven from outside). </summary>
    public void ForgetProfile()
        => _lastProfileId = null;
}
