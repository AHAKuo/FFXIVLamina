using Lamina.Ipc;
using Lamina.Models;

namespace Lamina.Runtime.Engine;

/// <summary>
/// At most one redraw per apply, only when mods changed. Coalesces requests and defers while a redraw would be jarring
/// (combat, cutscene, loading, mounted / flying, GPose) according to the <see cref="RedrawPolicy"/> (TDD 7.1 #7).
/// </summary>
public sealed class RedrawScheduler
{
    private readonly IPenumbraBridge _penumbra;
    private readonly Func<int?> _localPlayerIndex;
    private readonly Action<string>? _log;

    /// <param name="penumbra"> Penumbra bridge used to redraw. </param>
    /// <param name="localPlayerIndex"> Object index of the local player, or null when there is none. </param>
    /// <param name="log"> Optional debug logger. </param>
    public RedrawScheduler(IPenumbraBridge penumbra, Func<int?> localPlayerIndex, Action<string>? log = null)
    {
        _penumbra         = penumbra;
        _localPlayerIndex = localPlayerIndex;
        _log              = log;
    }

    /// <summary> True while a redraw is waiting for a safe moment (or for a manual /lam redraw under the Never policy). </summary>
    public bool Pending { get; private set; }

    /// <summary> How many redraws were actually sent. </summary>
    public int RedrawsSent { get; private set; }

    /// <summary> Asks for a redraw. Several requests before the next tick collapse into one. </summary>
    public void Request()
        => Pending = true;

    /// <summary> Drops a pending request without redrawing. </summary>
    public void Cancel()
        => Pending = false;

    /// <summary> Decides, once per frame, whether a pending redraw can go out now. </summary>
    /// <returns> True when a redraw was sent this tick. </returns>
    public bool Tick(GameStateSnapshot snapshot, RedrawPolicy policy)
    {
        if (!Pending)
            return false;

        switch (policy)
        {
            case RedrawPolicy.Immediate:
                return RedrawNow();
            case RedrawPolicy.DeferWhenUnsafe:
                return !snapshot.RedrawUnsafe && RedrawNow();
            default:
                return false;
        }
    }

    /// <summary> Redraws right away regardless of policy and clears the pending flag. </summary>
    /// <returns> True when the redraw was sent. </returns>
    public bool RedrawNow()
    {
        if (!_penumbra.Available)
        {
            _log?.Invoke("Redraw requested but Penumbra is unavailable; dropping the request.");
            Pending = false;
            return false;
        }

        if (_localPlayerIndex() is not { } index)
        {
            // No player to redraw right now; keep the request for when there is one.
            return false;
        }

        _penumbra.RedrawObject(index);
        RedrawsSent++;
        Pending = false;
        return true;
    }
}
