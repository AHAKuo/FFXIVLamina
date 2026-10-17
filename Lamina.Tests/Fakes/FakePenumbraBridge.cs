using System.Threading.Tasks;
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

    public PenumbraApiEc RemoveTemporaryModSettings(Guid collectionId, string modDirectory)
    {
        Record(nameof(RemoveTemporaryModSettings), collectionId, modDirectory);
        if (!Available)
            return PenumbraApiEc.UnknownError;
        if (!Collections.ContainsKey(collectionId))
            return PenumbraApiEc.CollectionMissing;

        return TemporarySettings.TryGetValue(collectionId, out var perMod) && perMod.Remove(modDirectory)
            ? PenumbraApiEc.Success
            : PenumbraApiEc.NothingChanged;
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
        Guid collectionId, string modDirectory, bool ignoreTemporary = false)
    {
        Record(nameof(GetCurrentModSettings), collectionId, modDirectory, ignoreTemporary);
        if (!Available || !Collections.ContainsKey(collectionId) || !Mods.ContainsKey(modDirectory))
            return null;

        if (!ignoreTemporary && TemporarySettings.TryGetValue(collectionId, out var temp) && temp.TryGetValue(modDirectory, out var t))
            return (t.Enabled, t.Priority, t.Settings, false, true);

        if (PermanentSettings.TryGetValue(collectionId, out var perm) && perm.TryGetValue(modDirectory, out var p))
            return (p.Enabled, p.Priority, p.Settings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), p.Inherited, false);

        // Not configured in the collection: Penumbra answers with no settings tuple.
        return null;
    }

    // ---- decal support ----

    /// <summary> Resource tree returned for every object (null by default). </summary>
    public Penumbra.Api.ResourceTreeDto? ResourceTree { get; set; }

    /// <summary> Game path → resolved disk path used by <see cref="ResolveGameObjectPath"/>; unlisted paths resolve to themselves. </summary>
    public Dictionary<string, string> ResolvedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Collection id → (tag, priority) → redirects, as written by <see cref="AddTemporaryMod"/>. </summary>
    public Dictionary<Guid, Dictionary<(string Tag, int Priority), Dictionary<string, string>>> TemporaryMods { get; } = new();

    /// <summary> Every texture conversion requested: (input file or null, width or 0, output file, type, mip maps). </summary>
    public List<(string? InputFile, int Width, string OutputFile, TextureType Type, bool MipMaps)> Conversions { get; } = new();

    /// <summary> When positive, conversions write this many bytes to the output file so the file exists. </summary>
    public int ConversionOutputBytes { get; set; } = 16;

    public Penumbra.Api.ResourceTreeDto? GetGameObjectResourceTree(int objectIndex, bool withUiData = true)
    {
        Record(nameof(GetGameObjectResourceTree), objectIndex, withUiData);
        return Available ? ResourceTree : null;
    }

    public string ResolveGameObjectPath(string gamePath, int objectIndex)
    {
        Record(nameof(ResolveGameObjectPath), gamePath, objectIndex);
        if (!Available)
            return gamePath;
        foreach (var (_, mods) in TemporaryMods)
            foreach (var (_, redirects) in mods)
                if (redirects.TryGetValue(gamePath, out var temp))
                    return temp;
        return ResolvedPaths.TryGetValue(gamePath, out var resolved) ? resolved : gamePath;
    }

    public PenumbraApiEc AddTemporaryMod(string tag, Guid collectionId, IReadOnlyDictionary<string, string> redirects, int priority)
    {
        Record(nameof(AddTemporaryMod), tag, collectionId, redirects, priority);
        if (!Available)
            return PenumbraApiEc.UnknownError;
        if (!Collections.ContainsKey(collectionId))
            return PenumbraApiEc.CollectionMissing;
        if (!TemporaryMods.TryGetValue(collectionId, out var mods))
            TemporaryMods[collectionId] = mods = new Dictionary<(string, int), Dictionary<string, string>>();
        mods[(tag, priority)] = redirects.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return PenumbraApiEc.Success;
    }

    public PenumbraApiEc RemoveTemporaryMod(string tag, Guid collectionId, int priority)
    {
        Record(nameof(RemoveTemporaryMod), tag, collectionId, priority);
        if (!Available)
            return PenumbraApiEc.UnknownError;
        if (!Collections.ContainsKey(collectionId))
            return PenumbraApiEc.CollectionMissing;
        return TemporaryMods.TryGetValue(collectionId, out var mods) && mods.Remove((tag, priority))
            ? PenumbraApiEc.Success
            : PenumbraApiEc.NothingChanged;
    }

    public Task ConvertTextureFile(string inputFile, string outputFile, TextureType textureType, bool mipMaps)
    {
        Record(nameof(ConvertTextureFile), inputFile, outputFile, textureType, mipMaps);
        Conversions.Add((inputFile, 0, outputFile, textureType, mipMaps));
        WriteConversionOutput(outputFile);
        return Task.CompletedTask;
    }

    public Task ConvertTextureData(byte[] rgbaData, int width, string outputFile, TextureType textureType, bool mipMaps)
    {
        Record(nameof(ConvertTextureData), rgbaData.Length, width, outputFile, textureType, mipMaps);
        Conversions.Add((null, width, outputFile, textureType, mipMaps));
        WriteConversionOutput(outputFile);
        return Task.CompletedTask;
    }

    private void WriteConversionOutput(string outputFile)
    {
        if (ConversionOutputBytes <= 0)
            return;
        var dir = System.IO.Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(outputFile, new byte[ConversionOutputBytes]);
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
