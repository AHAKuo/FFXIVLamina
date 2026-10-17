using Lamina.Models;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Which layer of each pass's random section (<see cref="Pass.RandomLayers"/>) is in effect, keyed by pass id. A pick is
/// rolled with equal chance among the enabled layers when a pass contributes without one, kept while it keeps contributing
/// (so a changing game state does not reshuffle it) and forgotten when the pass stops contributing, so the next time it
/// kicks in it rolls again. Lives for the plugin session only; a reload rolls fresh.
/// </summary>
public sealed class RandomLayerPicks
{
    private readonly Dictionary<Guid, Guid> _picks = new();
    private readonly Func<int, int> _next;

    /// <param name="next"> Random index source: exclusive upper bound → index. Defaults to <see cref="Random.Shared"/>. </param>
    public RandomLayerPicks(Func<int, int>? next = null)
        => _next = next ?? (max => Random.Shared.Next(max));

    /// <summary> The id of the layer in effect for a pass, or null when the pass has no pick (never contributed, or its section is empty). </summary>
    public Guid? Current(Guid passId)
        => _picks.TryGetValue(passId, out var id) ? id : null;

    /// <summary>
    /// The random layer the pass uses right now: its stored pick when that is still an enabled member of the section, else
    /// a fresh roll. With <paramref name="commit"/> the roll is stored; without it (previews) nothing changes and a pass
    /// that has no pick yet shows its first enabled random layer, so the preview is stable instead of flickering.
    /// Returns null when the section has no enabled layer.
    /// </summary>
    public Layer? Choose(Pass pass, bool commit)
    {
        var candidates = pass.RandomLayers.Where(l => l.Enabled).ToList();
        if (candidates.Count == 0)
        {
            if (commit)
                _picks.Remove(pass.Id);
            return null;
        }

        if (_picks.TryGetValue(pass.Id, out var id) && candidates.FirstOrDefault(l => l.Id == id) is { } kept)
            return kept;
        if (!commit)
            return candidates[0];

        var pick = candidates[Math.Clamp(_next(candidates.Count), 0, candidates.Count - 1)];
        _picks[pass.Id] = pick.Id;
        return pick;
    }

    /// <summary> Forgets the picks of every pass not in <paramref name="contributingPassIds"/>: they roll again when they next contribute. </summary>
    public void Retain(IEnumerable<Guid> contributingPassIds)
    {
        var keep = contributingPassIds as ICollection<Guid> ?? contributingPassIds.ToList();
        foreach (var id in _picks.Keys.Where(k => !keep.Contains(k)).ToList())
            _picks.Remove(id);
    }

    /// <summary> Forgets the pick of one pass (or of every pass with null) so the next composition rolls again. </summary>
    public void Reroll(Guid? passId = null)
    {
        if (passId is { } id)
            _picks.Remove(id);
        else
            _picks.Clear();
    }
}
