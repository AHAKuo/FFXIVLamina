using Lamina.Models;

namespace Lamina.Runtime;

/// <summary>
/// Evaluates condition stacks against a snapshot. Owns hold / linger timers, so the same stack can flip
/// later without a new snapshot: the engine re-evaluates when <see cref="TimeUntilNextChange"/> elapses.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary> Evaluates with hold / linger applied. Empty stack = true. Results are cached per (node id, snapshot sequence). </summary>
    bool Evaluate(ConditionStack stack, GameStateSnapshot snapshot);

    /// <summary> Evaluates ignoring hold / linger timers (used by previews and /lam why). </summary>
    bool EvaluateInstant(ConditionStack stack, GameStateSnapshot snapshot);

    /// <summary> Time until a pending hold or linger timer changes some result, or null when nothing is pending. </summary>
    TimeSpan? TimeUntilNextChange { get; }

    /// <summary> Forget all timers (profile switch, master toggle). </summary>
    void Reset();
}

/// <summary> Which groups and passes matched for one evaluation. Attached to the VirtualDesign's metadata by the compositor. </summary>
public sealed record EvaluationReport(
    Guid ProfileId,
    Guid? ActiveGroupId,
    IReadOnlyList<Guid> ActiveFinalGroupIds,
    IReadOnlyList<Guid> MatchedPassIds,
    IReadOnlyList<(Guid LayerId, string Reason)> SkippedLayers);
