using Lamina.Models;

namespace Lamina.Runtime.State;

/// <summary>
/// Evaluates condition stacks against a <see cref="GameStateSnapshot"/> and owns the hold / linger timers.
/// <para>
/// Semantics per leaf condition:
/// <list type="bullet">
/// <item><b>raw</b> = <see cref="Condition.Matches"/> on the snapshot.</item>
/// <item><b>Hold</b>: after raw flips to true, the timed result becomes true only once raw has stayed true for the hold
/// duration, measured from the observed flip. A hold of 0 is instantaneous.</item>
/// <item><b>Linger</b>: once the timed result was true, it stays true for the linger duration after raw turned false.</item>
/// <item><b>Defaults</b>: the config's default hold / linger apply only to transient kinds (<see cref="Condition.IsTransient"/>:
/// movement, body state, emote, activity, status effects). Stable kinds (time, place, job, ...) use 0 unless
/// <see cref="Condition.HoldSeconds"/> / <see cref="Condition.LingerSeconds"/> are set explicitly. Explicit values always win.</item>
/// <item><b>Fresh leaves</b>: a leaf seen for the first time (or seen again after it was skipped for at least one snapshot,
/// e.g. because its group lost the first-true-wins switch) starts in its current raw state as if it had been there forever:
/// raw true is true immediately (no hold), raw false is false. Timers from before the gap are discarded, so a leaf cannot
/// linger on state it stopped observing minutes ago.</item>
/// <item><b>Negate</b> inverts the timed result (so "not mounted" with linger stays false for a while after dismounting).</item>
/// <item>Disabled leaves are neutral: they are ignored by All / Any. A group with only neutral children is true.</item>
/// <item>A group's Negate inverts the group. An empty stack is true.</item>
/// </list>
/// Timers are keyed by leaf Id and survive across snapshots. Results are cached per (node id, snapshot sequence, clock reading),
/// so repeated evaluations in the same tick with the same clock reading cost nothing, while timer wake-ups on the same
/// snapshot (same sequence, later clock) re-evaluate correctly. The engine is expected to read the clock once per tick.
/// </para>
/// </summary>
public sealed class ConditionEvaluator : IConditionEvaluator
{
    private sealed class LeafState
    {
        public bool LastRaw;
        public DateTime RawSince;
        public bool Timed;
        public DateTime? LingerUntil;
        public DateTime? HoldUntil;
        public long LastTouchedSequence;
    }

    private readonly Func<DateTime> _clock;
    private readonly Func<LaminaConfig> _config;
    private readonly Dictionary<Guid, LeafState> _states = new();
    private readonly Dictionary<Guid, bool?> _cache = new();
    private long _cacheSequence = -1;
    private long _previousSequence = -1;
    private DateTime _cacheNow = DateTime.MinValue;

    /// <param name="clock"> Returns the current time; injected so tests can drive timers and the engine can pass one reading per tick. </param>
    /// <param name="config"> Supplies the default hold / linger seconds for transient kinds. </param>
    public ConditionEvaluator(Func<DateTime> clock, Func<LaminaConfig> config)
    {
        _clock  = clock;
        _config = config;
    }

    /// <inheritdoc/>
    public bool Evaluate(ConditionStack stack, GameStateSnapshot snapshot)
    {
        if (stack.Root is null)
            return true;

        var now = _clock();
        if (snapshot.Sequence != _cacheSequence)
        {
            _previousSequence = _cacheSequence;
            _cacheSequence    = snapshot.Sequence;
            _cacheNow         = now;
            _cache.Clear();
        }
        else if (now != _cacheNow)
        {
            _cacheNow = now;
            _cache.Clear();
        }

        return EvaluateNode(stack.Root, snapshot, now, timed: true) ?? true;
    }

    /// <inheritdoc/>
    public bool EvaluateInstant(ConditionStack stack, GameStateSnapshot snapshot)
        => stack.Root is null || (EvaluateNode(stack.Root, snapshot, _clock(), timed: false) ?? true);

    /// <inheritdoc/>
    public TimeSpan? TimeUntilNextChange
    {
        get
        {
            var now = _clock();
            TimeSpan? best = null;
            foreach (var state in _states.Values)
            {
                if (state.LastTouchedSequence < _cacheSequence)
                    continue;
                Consider(state.HoldUntil);
                Consider(state.LingerUntil);
            }

            return best;

            void Consider(DateTime? deadline)
            {
                if (deadline is not { } d)
                    return;
                var remaining = d - now;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;
                if (best is null || remaining < best.Value)
                    best = remaining;
            }
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _states.Clear();
        _cache.Clear();
        _cacheSequence    = -1;
        _previousSequence = -1;
        _cacheNow         = DateTime.MinValue;
    }

    /// <summary> Returns null for neutral nodes (disabled leaves). Groups with only neutral children are not neutral: they are true. </summary>
    private bool? EvaluateNode(ConditionNode node, GameStateSnapshot s, DateTime now, bool timed)
    {
        switch (node)
        {
            case ConditionGroup group:
            {
                bool? result = null;
                foreach (var child in group.Children)
                {
                    var r = EvaluateNode(child, s, now, timed);
                    if (r is null)
                        continue;
                    if (group.Op == GroupOp.All)
                    {
                        result = (result ?? true) && r.Value;
                        if (!result.Value)
                            break;
                    }
                    else
                    {
                        result = (result ?? false) || r.Value;
                        if (result.Value)
                            break;
                    }
                }

                var value = result ?? true;
                return group.Negate ? !value : value;
            }
            case Condition leaf:
            {
                if (!leaf.Enabled)
                    return null;

                bool value;
                if (timed)
                {
                    if (_cache.TryGetValue(leaf.Id, out var cached))
                        return cached;
                    value = EvaluateTimed(leaf, s, now);
                    var negated = leaf.Negate ? !value : value;
                    _cache[leaf.Id] = negated;
                    return negated;
                }

                value = SafeMatches(leaf, s);
                return leaf.Negate ? !value : value;
            }
            default:
                return null;
        }
    }

    /// <summary> Hold and linger for a leaf: explicit values win, config defaults apply to transient kinds only, stable kinds get 0. </summary>
    private (TimeSpan Hold, TimeSpan Linger) Durations(Condition leaf)
    {
        var config = _config();
        var hold   = leaf.HoldSeconds ?? (leaf.IsTransient ? config.DefaultHoldSeconds : 0f);
        var linger = leaf.LingerSeconds ?? (leaf.IsTransient ? config.DefaultLingerSeconds : 0f);
        return (TimeSpan.FromSeconds(Math.Max(0, hold)), TimeSpan.FromSeconds(Math.Max(0, linger)));
    }

    private bool EvaluateTimed(Condition leaf, GameStateSnapshot s, DateTime now)
    {
        var raw            = SafeMatches(leaf, s);
        var (hold, linger) = Durations(leaf);

        // A leaf is fresh when never seen, or when it was skipped for at least one snapshot since it was last touched
        // (its group lost the switch, or an All / Any short-circuited past it). Fresh leaves start in their raw state as if
        // it had been that way forever: no pending hold, no stale linger.
        var fresh = !_states.TryGetValue(leaf.Id, out var state) || state.LastTouchedSequence < _previousSequence;
        if (fresh)
        {
            state = new LeafState
            {
                LastRaw  = raw,
                RawSince = raw ? now - hold : now,
                Timed    = raw,
            };
            _states[leaf.Id] = state;
        }
        else if (state!.LastRaw != raw)
        {
            state.LastRaw  = raw;
            state.RawSince = now;
            if (!raw && state.Timed)
            {
                // Raw just turned false: start lingering (or drop immediately when there is no linger).
                if (linger <= TimeSpan.Zero)
                    state.Timed = false;
                else
                    state.LingerUntil = now + linger;
            }
        }

        state.LastTouchedSequence = s.Sequence;
        state.HoldUntil           = null;

        if (raw)
        {
            var holdMet = hold <= TimeSpan.Zero || now - state.RawSince >= hold;
            if (holdMet)
            {
                state.Timed       = true;
                state.LingerUntil = null;
                return true;
            }

            state.HoldUntil = state.RawSince + hold;
        }

        // Raw is false, or true with the hold still pending: only a running linger keeps the timed result true.
        if (!state.Timed)
        {
            state.LingerUntil = null;
            return false;
        }

        // Timed is true without a linger deadline: raw flipped false and back to true between evaluations; treat as continuous.
        if (state.LingerUntil is null)
            return true;

        if (now < state.LingerUntil.Value)
            return true;

        state.Timed       = false;
        state.LingerUntil = null;
        return false;
    }

    private static bool SafeMatches(Condition leaf, GameStateSnapshot s)
    {
        try
        {
            return leaf.Matches(s);
        }
        catch
        {
            return false;
        }
    }
}
