using Lamina.Decals;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Publishes decals against the outfit that is actually drawn. The applier parks decals in
/// <see cref="AppliedStateStore.PendingDecals"/> whenever a mods redraw is involved, because the publisher snapshots the skin
/// meshes and resolves the skin texture of the character on screen, which that redraw is about to replace (decals used to
/// break on a design's first apply until a manual reapply). This coordinator is driven by the engine: a trigger (Penumbra
/// redrew the player, or the draw object identity changed between polls: login, zone change, gear swap) starts a wait until
/// the drawn models report loaded (bounded by <see cref="LoadTimeout"/>), then it publishes the pending decals, or republishes
/// the applied ones when the draw object no longer matches the one the last publish was prepared against. The engine's own
/// completion redraw triggers it again, but by then the identity equals the recorded one, so it converges without a loop.
/// Dalamud-free; every game access is injected.
/// </summary>
public sealed class DecalFollowUp
{
    /// <summary> How long to wait for the models after a trigger before publishing against whatever is drawn. </summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    private readonly AppliedStateStore _store;
    private readonly Func<DateTime> _now;
    private readonly Func<DrawObjectIdentity?> _drawObject;
    private readonly Func<bool> _ready;
    private readonly Func<bool> _busy;
    private readonly Action<IReadOnlyList<DecalSpec>> _publish;
    private readonly Action<string>? _log;

    private bool _waiting;
    private DateTime? _loadingSince;
    private string? _lastTickKey;
    private string? _lastPolledKey;
    private bool _recheckAfterPublish;
    private bool _reloadDuringPublish;

    /// <param name="store"> Applied state: pending decals, the applied design and the recorded draw object identity. </param>
    /// <param name="now"> UTC clock. </param>
    /// <param name="drawObject"> The player's draw object identity right now, null while none is drawn. </param>
    /// <param name="ready"> True when a publish can go out: Lamina enabled, Penumbra available, collection assigned, player present. </param>
    /// <param name="busy"> True while the publisher is baking. </param>
    /// <param name="publish"> Starts a publish of the given decals for the player in the Lamina collection. </param>
    /// <param name="log"> Optional debug logger. </param>
    public DecalFollowUp(AppliedStateStore store, Func<DateTime> now, Func<DrawObjectIdentity?> drawObject, Func<bool> ready, Func<bool> busy,
        Action<IReadOnlyList<DecalSpec>> publish, Action<string>? log = null)
    {
        _store      = store;
        _now        = now;
        _drawObject = drawObject;
        _ready      = ready;
        _busy       = busy;
        _publish    = publish;
        _log        = log;
    }

    /// <summary> True between a trigger and its decision (waiting for the models). </summary>
    public bool Waiting
        => _waiting;

    /// <summary> How many publishes this coordinator started. </summary>
    public int Publishes { get; private set; }

    /// <summary>
    /// The player's character was redrawn or its draw object changed. A publish in flight was prepared against what was drawn
    /// before, so its completion asks for a redraw even when the redirects did not change (the game may have reloaded the
    /// textures while Lamina's decal mod was out of the collection).
    /// </summary>
    public void Notice(string reason)
    {
        if (_busy())
            _reloadDuringPublish = true;

        Arm(reason);
    }

    /// <summary> Slow-tick poll: compares the draw object identity with the last poll and notices a change (login, zone change, gear swap, load progress). </summary>
    public void Poll()
    {
        var key = _drawObject()?.Key;
        if (string.Equals(key, _lastPolledKey, StringComparison.Ordinal))
            return;

        _lastPolledKey = key;
        Notice("draw object changed");
    }

    /// <summary> Per-frame step: while armed, waits for the models to load (or the timeout), then decides. </summary>
    public void Tick()
    {
        if (!_waiting)
            return;

        if (!_store.DecalsApplied)
        {
            // Cleaned up meanwhile: nothing to publish.
            _waiting = false;
            return;
        }

        var state = _drawObject();
        if (state is null)
        {
            // Nothing drawn (mid-redraw, loading screen): no timeout runs while there is nothing to publish against.
            _loadingSince = null;
            _lastTickKey  = null;
            return;
        }

        // Loaded means every model reports loaded and the identity held for two consecutive ticks.
        var loaded = state.Loaded && string.Equals(state.Key, _lastTickKey, StringComparison.Ordinal);
        _lastTickKey = state.Key;
        if (!loaded)
        {
            _loadingSince ??= _now();
            if (_now() - _loadingSince.Value < LoadTimeout)
                return;

            _log?.Invoke("Decal follow-up: the player's models are still loading after the timeout; publishing against what is drawn.");
        }

        _waiting       = false;
        _loadingSince  = null;
        _lastTickKey   = null;
        _lastPolledKey = state.Key;
        Decide(state.Key);
    }

    /// <summary>
    /// The publisher completed. Re-arms when a trigger arrived during the publish, and says whether the engine should redraw:
    /// when surfaces were redirected and either the redirects changed or the character was reloaded while the publish ran.
    /// </summary>
    public bool OnPublishCompleted(DecalPublishResult result)
    {
        var reloaded = _reloadDuringPublish;
        _reloadDuringPublish = false;
        if (_recheckAfterPublish)
        {
            _recheckAfterPublish = false;
            Arm("publish completed");
        }

        return RedrawWanted(result, reloaded);
    }

    /// <summary> Whether a completed publish needs a redraw: something was redirected, and the redirects differ from what the game loaded (or it reloaded meanwhile). </summary>
    public static bool RedrawWanted(DecalPublishResult result, bool reloadedDuringPublish)
        => result.Published > 0 && (result.RedirectsChanged || reloadedDuringPublish);

    /// <summary> Drops any wait and bookkeeping (cleanup, logout). </summary>
    public void Reset()
    {
        _waiting             = false;
        _loadingSince        = null;
        _lastTickKey         = null;
        _recheckAfterPublish = false;
        _reloadDuringPublish = false;
    }

    private void Arm(string reason)
    {
        if (!_store.DecalsApplied || _waiting)
            return;

        _waiting      = true;
        _loadingSince = null;
        _lastTickKey  = null;
        _log?.Invoke($"Decal follow-up armed: {reason}.");
    }

    private void Decide(string key)
    {
        if (!_ready())
        {
            // Pending decals stay in the store; the next trigger (collection assigned, player back) tries again.
            _log?.Invoke("Decal follow-up: not ready to publish; waiting for the next trigger.");
            return;
        }

        if (_busy())
        {
            _recheckAfterPublish = true;
            return;
        }

        if (_store.PendingDecals is { } pending)
        {
            Start(pending, key, "pending decals");
            return;
        }

        if (_store.LastDesign is { HasDecals: true } design && !string.Equals(key, _store.DecalsDrawKey, StringComparison.Ordinal))
            Start(design.Decals.Select(f => f.Value).ToList(), key, "the drawn outfit changed");
    }

    private void Start(IReadOnlyList<DecalSpec> decals, string key, string why)
    {
        _store.PendingDecals = null;
        _store.DecalsDrawKey = key;
        Publishes++;
        _log?.Invoke($"Decal follow-up: publishing {decals.Count} decal(s) ({why}).");
        _publish(decals);
    }
}
