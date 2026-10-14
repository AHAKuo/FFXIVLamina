using Lamina.Ipc;
using Penumbra.Api.Enums;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Penumbra bridge. Collections, mods and temporary settings are plain dictionaries the test can inspect. </summary>
public sealed class FakePenumbraBridge : FakeBridgeBase, IPenumbraBridge
{
    public FakePenumbraBridge()
        : base("Penumbra")
    { }

    /// <summary> One temporary mod setting as written by <see cref="SetTemporaryModSettings"/>. </summary>
    public sealed record TempSetting(bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings);

    /// <summary> Collection id → name. </summary>
    public Dictionary<Guid, string> Collections { get; } = new();

    /// <summary> Object index → individually assigned collection. </summary>
    public Dictionary<int, Guid> ObjectCollections { get; } = new();

    /// <summary> Object indices that exist. Others report ObjectValid = false. Empty = every index is valid. </summary>
    public HashSet<int> ValidObjects { get; } = new();

    /// <summary> The collection reported when an object has no individual assignment. </summary>
    public (Guid Id, string Name) DefaultCollection { get; set; } = (Guid.Empty, "Default");

    /// <summary> Mod directory → mod name. </summary>
    public Dictionary<string, string> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Mod directory → available option groups. </summary>
    public Dictionary<string, Dictionary<string, (string[] Options, GroupType Type)>> AvailableSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Mod directory → changed items. </summary>
    public Dictionary<string, Dictionary<string, object?>> ChangedItems { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Collection id → mod directory → the temporary setting Lamina wrote. </summary>
    public Dictionary<Guid, Dictionary<string, TempSetting>> TemporarySettings { get; } = new();

    /// <summary> Collection id → mod directory → permanent settings (enabled, priority, settings, inherited). </summary>
    public Dictionary<Guid, Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited)>> PermanentSettings { get; } = new();

    /// <summary> Object indices redrawn through <see cref="RedrawObject"/>, in order. </summary>
    public List<int> Redraws { get; } = new();

    /// <summary> Result returned by <see cref="SetTemporaryModSettings"/> when the mod exists. </summary>
    public PenumbraApiEc SetTemporaryResult { get; set; } = PenumbraApiEc.Success;

    public event Action<string>? ModAdded;
    public event Action<string>? ModDeleted;
    public event Action<string, string>? ModMoved;
    public event Action<nint, int>? GameObjectRedrawn;

    /// <summary> Registers a collection and returns its id. </summary>
    public Guid AddCollection(string name)
    {
        var id = Guid.NewGuid();
        Collections[id] = name;
        return id;
    }

    /// <summary> Registers a mod (directory = name unless given). </summary>
    public void AddMod(string directory, string? name = null)
        => Mods[directory] = name ?? directory;

    /// <summary> Temporary settings currently in a collection (empty when none). </summary>
    public IReadOnlyDictionary<string, TempSetting> TempSettingsIn(Guid collectionId)
        => TemporarySettings.TryGetValue(collectionId, out var d) ? d : new Dictionary<string, TempSetting>();

    public IReadOnlyDictionary<Guid, string> GetCollections()
    {
        Record(nameof(GetCollections));
        return Available ? new Dictionary<Guid, string>(Collections) : new Dictionary<Guid, string>();
    }

    public Guid? FindCollection(string name)
    {
        Record(nameof(FindCollection), name);
        if (!Available)
            return null;

        foreach (var (id, collectionName) in Collections)
            if (string.Equals(collectionName, name, StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    public (bool ObjectValid, bool IndividualSet, Guid CollectionId, string CollectionName) GetCollectionForObject(int objectIndex)
    {
        Record(nameof(GetCollectionForObject), objectIndex);
        if (!Available)
            return (false, false, Guid.Empty, string.Empty);

        var valid = ValidObjects.Count == 0 || ValidObjects.Contains(objectIndex);
        if (!valid)
            return (false, false, Guid.Empty, string.Empty);

        if (ObjectCollections.TryGetValue(objectIndex, out var id))
            return (true, true, id, Collections.TryGetValue(id, out var n) ? n : string.Empty);

        return (true, false, DefaultCollection.Id, DefaultCollection.Name);
    }

    public PenumbraApiEc SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        Record(nameof(SetTemporaryModSettings), collectionId, modDirectory, enabled, priority, settings);
        if (!Available)
            return PenumbraApiEc.UnknownError;
        if (!Collections.ContainsKey(collectionId))
            return PenumbraApiEc.CollectionMissing;
        if (!Mods.ContainsKey(modDirectory))
            return PenumbraApiEc.ModMissing;
        if (SetTemporaryResult != PenumbraApiEc.Success)
            return SetTemporaryResult;

        if (!TemporarySettings.TryGetValue(collectionId, out var perMod))
            TemporarySettings[collectionId] = perMod = new Dictionary<string, TempSetting>(StringComparer.OrdinalIgnoreCase);

        var copy = settings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList());
        perMod[modDirectory] = new TempSetting(enabled, priority, copy);
        return PenumbraApiEc.Success;
    }

    public PenumbraApiEc RemoveAllTemporaryModSettings(Guid collectionId)
    {
        Record(nameof(RemoveAllTemporaryModSettings), collectionId);
        if (!Available)
            return PenumbraApiEc.UnknownError;
        if (!Collections.ContainsKey(collectionId))
            return PenumbraApiEc.CollectionMissing;

        if (!TemporarySettings.TryGetValue(collectionId, out var perMod) || perMod.Count == 0)
            return PenumbraApiEc.NothingChanged;

        perMod.Clear();
        return PenumbraApiEc.Success;
    }

    public void RedrawObject(int objectIndex)
    {
        Record(nameof(RedrawObject), objectIndex);
        if (!Available)
            return;

        Redraws.Add(objectIndex);
    }

    public IReadOnlyDictionary<string, string> GetModList()
    {
        Record(nameof(GetModList));
        return Available ? new Dictionary<string, string>(Mods, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>();
    }

    public IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? GetAvailableModSettings(string modDirectory)
    {
        Record(nameof(GetAvailableModSettings), modDirectory);
        if (!Available || !Mods.ContainsKey(modDirectory))
            return null;

        return AvailableSettings.TryGetValue(modDirectory, out var groups)
            ? new Dictionary<string, (string[], GroupType)>(groups)
            : new Dictionary<string, (string[], GroupType)>();
    }

    public IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory)
    {
        Record(nameof(GetChangedItems), modDirectory);
        if (!Available || !ChangedItems.TryGetValue(modDirectory, out var items))
            return new Dictionary<string, object?>();

        return new Dictionary<string, object?>(items);
    }

    public (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings, bool Inherited, bool Temporary)? GetCurrentModSettings(
        Guid collectionId, string modDirectory)
    {
        Record(nameof(GetCurrentModSettings), collectionId, modDirectory);
        if (!Available || !Collections.ContainsKey(collectionId) || !Mods.ContainsKey(modDirectory))
            return null;

        if (TemporarySettings.TryGetValue(collectionId, out var temp) && temp.TryGetValue(modDirectory, out var t))
            return (t.Enabled, t.Priority, t.Settings, false, true);

        if (PermanentSettings.TryGetValue(collectionId, out var perm) && perm.TryGetValue(modDirectory, out var p))
            return (p.Enabled, p.Priority, p.Settings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), p.Inherited, false);

        return (false, 0, new Dictionary<string, IReadOnlyList<string>>(), false, false);
    }

    public void RaiseModAdded(string modDirectory)
        => ModAdded?.Invoke(modDirectory);

    public void RaiseModDeleted(string modDirectory)
        => ModDeleted?.Invoke(modDirectory);

    public void RaiseModMoved(string oldDirectory, string newDirectory)
        => ModMoved?.Invoke(oldDirectory, newDirectory);

    public void RaiseGameObjectRedrawn(nint address, int objectIndex)
        => GameObjectRedrawn?.Invoke(address, objectIndex);
}
