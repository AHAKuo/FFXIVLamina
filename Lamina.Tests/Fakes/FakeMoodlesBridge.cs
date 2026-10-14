using Lamina.Ipc;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Moodles bridge. Tracks which Moodles are active per actor address. </summary>
public sealed class FakeMoodlesBridge : FakeBridgeBase, IMoodlesBridge, IMoodlesStatusEvents
{
    public FakeMoodlesBridge()
        : base("Moodles")
    { }

    /// <summary> Registered Moodles. </summary>
    public List<(Guid Id, uint IconId, string FullPath, string Title)> Moodles { get; } = new();

    /// <summary> Registered presets, with the Moodles each expands to. </summary>
    public Dictionary<Guid, (string FullPath, List<Guid> Statuses)> Presets { get; } = new();

    /// <summary> Actor address → set of active Moodle ids. </summary>
    public Dictionary<nint, HashSet<Guid>> Active { get; } = new();

    /// <summary> Actor address → presets currently applied through <see cref="ApplyPreset"/>. </summary>
    public Dictionary<nint, HashSet<Guid>> ActivePresets { get; } = new();

    public event Action<nint>? StatusManagerModified;

    /// <summary> Simulates Moodles reporting a status manager change on an actor. </summary>
    public void RaiseStatusManagerModified(nint actorAddress)
        => StatusManagerModified?.Invoke(actorAddress);

    /// <summary> Registers a Moodle and returns its id. </summary>
    public Guid AddMoodle(string title, string? path = null, uint icon = 0)
    {
        var id = Guid.NewGuid();
        Moodles.Add((id, icon, path ?? title, title));
        return id;
    }

    /// <summary> Registers a preset and returns its id. </summary>
    public Guid AddPreset(string path, params Guid[] statuses)
    {
        var id = Guid.NewGuid();
        Presets[id] = (path, statuses.ToList());
        return id;
    }

    /// <summary> Active Moodles on an actor (empty when none). </summary>
    public IReadOnlySet<Guid> ActiveOn(nint actor)
        => Active.TryGetValue(actor, out var set) ? set : new HashSet<Guid>();

    public IReadOnlyList<(Guid Id, uint IconId, string FullPath, string Title)> GetMoodles()
    {
        Record(nameof(GetMoodles));
        return Available ? Moodles.ToList() : [];
    }

    public IReadOnlyList<(Guid Id, string FullPath)> GetPresets()
    {
        Record(nameof(GetPresets));
        return Available ? Presets.Select(kv => (kv.Key, kv.Value.FullPath)).ToList() : [];
    }

    public void AddMoodle(nint actorAddress, Guid moodleId)
    {
        Record(nameof(AddMoodle), actorAddress, moodleId);
        if (!Available)
            return;

        Set(actorAddress).Add(moodleId);
    }

    public void ApplyPreset(nint actorAddress, Guid presetId)
    {
        Record(nameof(ApplyPreset), actorAddress, presetId);
        if (!Available || !Presets.TryGetValue(presetId, out var preset))
            return;

        var set = Set(actorAddress);
        foreach (var status in preset.Statuses)
            set.Add(status);
        if (!ActivePresets.TryGetValue(actorAddress, out var presets))
            ActivePresets[actorAddress] = presets = new HashSet<Guid>();
        presets.Add(presetId);
    }

    public void RemovePreset(nint actorAddress, Guid presetId)
    {
        Record(nameof(RemovePreset), actorAddress, presetId);
        if (!Available || !Presets.TryGetValue(presetId, out var preset))
            return;

        if (ActivePresets.TryGetValue(actorAddress, out var presets))
            presets.Remove(presetId);
        if (Active.TryGetValue(actorAddress, out var set))
            foreach (var status in preset.Statuses)
                set.Remove(status);
    }

    public void RemoveMoodles(nint actorAddress, IReadOnlyList<Guid> moodleIds)
    {
        Record(nameof(RemoveMoodles), actorAddress, moodleIds.ToList());
        if (!Available || !Active.TryGetValue(actorAddress, out var set))
            return;

        foreach (var id in moodleIds)
            set.Remove(id);
    }

    private HashSet<Guid> Set(nint actor)
    {
        if (!Active.TryGetValue(actor, out var set))
            Active[actor] = set = new HashSet<Guid>();
        return set;
    }
}
