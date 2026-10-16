using Lamina.Decals;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory decal publisher: records Publish / Clear calls and lets a test raise <see cref="Completed"/> as the real one would. </summary>
public sealed class FakeDecalPublisher : IDecalPublisher
{
    /// <summary> Every Publish call in order: collection, object index, the specs handed over (copied list, same instances) and the mods key. </summary>
    public List<(Guid Collection, int ObjectIndex, IReadOnlyList<DecalSpec> Decals, string? ModsKey)> Publishes { get; } = new();

    /// <summary> Every CleanUp call in order. </summary>
    public List<(Guid? Collection, DecalCleanupMode Mode, bool RemoveTemporaryMod)> CleanUps { get; } = new();

    /// <summary> What <see cref="CleanUp"/> returns. </summary>
    public DecalCleanupResult CleanupResult { get; set; } = new(0, 0, 0, false, Array.Empty<string>());

    /// <summary> Every Clear call in order. </summary>
    public List<Guid> Clears { get; } = new();

    public bool Busy { get; set; }

    public event Action<DecalPublishResult>? Completed;

    public void Publish(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals, string? modsKey = null)
        => Publishes.Add((collectionId, objectIndex, decals.ToList(), modsKey));

    public DecalCleanupResult CleanUp(Guid? collectionId, DecalCleanupMode mode, bool removeTemporaryMod)
    {
        CleanUps.Add((collectionId, mode, removeTemporaryMod));
        return CleanupResult with { TemporaryModRemoved = removeTemporaryMod || mode == DecalCleanupMode.Everything };
    }

    public void Clear(Guid collectionId)
        => Clears.Add(collectionId);

    /// <summary> Raises <see cref="Completed"/>. </summary>
    public void RaiseCompleted(DecalPublishResult result)
        => Completed?.Invoke(result);

    /// <summary> Raises a successful completion for <paramref name="published"/> surfaces. </summary>
    public void CompleteSuccess(int published = 1, bool redirectsChanged = true)
        => RaiseCompleted(new DecalPublishResult(true, published, Array.Empty<string>(), redirectsChanged));

    /// <summary> Raises a failed completion with the given problems. </summary>
    public void CompleteFailure(params string[] problems)
        => RaiseCompleted(new DecalPublishResult(false, 0, problems));
}
