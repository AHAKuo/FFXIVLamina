namespace Lamina.Runtime.Engine;

/// <summary>
/// One timestamp per framework tick. The engine advances it at the start of every update; the evaluator, applier and
/// cleanup read it, so every evaluation inside one tick sees the same "now" (and the evaluator's per-tick cache works).
/// </summary>
public sealed class TickClock
{
    private readonly Func<DateTime> _source;

    /// <param name="source"> Where the time comes from; defaults to <see cref="DateTime.UtcNow"/>. Tests inject a fixed clock. </param>
    public TickClock(Func<DateTime>? source = null)
    {
        _source = source ?? (() => DateTime.UtcNow);
        Now     = _source();
    }

    /// <summary> The current tick's timestamp (UTC). </summary>
    public DateTime Now { get; private set; }

    /// <summary> Reads the source and stores it as this tick's timestamp. </summary>
    public DateTime Advance()
        => Now = _source();

    /// <summary> <see cref="Now"/> as a delegate, for services that take a clock function. </summary>
    public DateTime Read()
        => Now;
}
