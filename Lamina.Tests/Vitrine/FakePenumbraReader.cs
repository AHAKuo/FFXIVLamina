using Penumbra.Api.Enums;
using Vitrine.Penumbra;

namespace Lamina.Tests.Vitrine;

/// <summary> In-memory Penumbra for catalog / design tests. </summary>
public sealed class FakePenumbraReader : IPenumbraReader
{
    public bool Available { get; set; } = true;
    public (int Breaking, int Feature)? ApiVersion { get; set; } = (5, 0);
    public string ModRoot { get; set; } = string.Empty;
    public Dictionary<string, string> ModList { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ModMeta> Metas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, object?>> ChangedItems { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, (string[] Options, int GroupType)>> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Guid, string> Collections { get; } = new();
    public Dictionary<Guid, Dictionary<string, ModSettingsInCollection>> CollectionSettings { get; } = new();
    public (Guid Id, string Name, bool IndividualSet)? PlayerCollection { get; set; }
    public List<string> Calls { get; } = new();
    public int ChangedItemCalls { get; private set; }

    public event Action? AvailabilityChanged;
    public event Action<string>? ModAdded;
    public event Action<string>? ModDeleted;
    public event Action<string, string>? ModMoved;

    public void Probe() => Calls.Add("Probe");

    public void RaiseAvailability(bool available)
    {
        Available = available;
        AvailabilityChanged?.Invoke();
    }

    public void RaiseModAdded(string dir) => ModAdded?.Invoke(dir);
    public void RaiseModDeleted(string dir) => ModDeleted?.Invoke(dir);
    public void RaiseModMoved(string o, string n) => ModMoved?.Invoke(o, n);

    public string GetModRootDirectory() => ModRoot;

    public IReadOnlyDictionary<string, string> GetModList() => ModList;

    public ModMeta? GetModMeta(string modDirectory, string modName) => Metas.GetValueOrDefault(modDirectory);

    public IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory)
    {
        ChangedItemCalls++;
        return ChangedItems.GetValueOrDefault(modDirectory) ?? new Dictionary<string, object?>();
    }

    public IReadOnlyDictionary<string, (string[] Options, int GroupType)>? GetAvailableModSettings(string modDirectory)
        => Settings.GetValueOrDefault(modDirectory);

    public IReadOnlyDictionary<Guid, string> GetCollections() => Collections;

    public IReadOnlyDictionary<string, ModSettingsInCollection>? GetAllModSettings(Guid collectionId)
        => CollectionSettings.GetValueOrDefault(collectionId);

    public (Guid Id, string Name, bool IndividualSet)? GetCollectionForObject(int objectIndex) => objectIndex == 0 ? PlayerCollection : null;

    public (Guid Id, string Name)? GetSpecialCollection(ApiCollectionType type) => null;

    public int SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings, string source, int key)
    {
        Calls.Add($"SetTemp:{collectionId}:{modDirectory}:{enabled}:{priority}:{key}");
        return 0;
    }

    /// <summary> Object indices redrawn through the fake. </summary>
    public List<int> Redraws { get; } = new();

    public void RedrawObject(int objectIndex)
        => Redraws.Add(objectIndex);

    public int RemoveTemporaryModSettings(Guid collectionId, string modDirectory, int key)
    {
        Calls.Add($"RemoveTemp:{collectionId}:{modDirectory}:{key}");
        return 0;
    }

    public void Dispose()
    { }
}
