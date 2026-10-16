using Lamina.Decals;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory decal publisher: records Publish / Clear calls and lets a test raise <see cref="Completed"/> as the real one would. </summary>
public sealed class FakeDecalPublisher : IDecalPublisher
{
    /// <summary> Every Publish call in order: collection, object index and the specs handed over (copied list, same instances). </summary>
    public List<(Guid Collection, int ObjectIndex, IReadOnlyList<DecalSpec> Decals)> Publishes { get; } = new();

    /// <summary> Every Clear call in order. </summary>
    public List<Guid> Clears { get; } = new();

    public bool Busy { get; set; }

    public event Action<DecalPublishResult>? Completed;

    public void Publish(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals)
        => Publishes.Add((collectionId, objectIndex, decals.ToList()));

    public void Clear(Guid collectionId)
        => Clears.Add(collectionId);

    /// <summary> Raises <see cref="Completed"/>. </summary>
    public void RaiseCompleted(DecalPublishResult result)
        => Completed?.Invoke(result);

    /// <summary> Raises a successful completion for <paramref name="published"/> surfaces. </summary>
    public void CompleteSuccess(int published = 1)
        => RaiseCompleted(new DecalPublishResult(true, published, Array.Empty<string>()));

    /// <summary> Raises a failed completion with the given problems. </summary>
    public void CompleteFailure(params string[] problems)
        => RaiseCompleted(new DecalPublishResult(false, 0, problems));
}
