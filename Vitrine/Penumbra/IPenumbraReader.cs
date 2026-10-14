namespace Vitrine.Penumbra;

/// <summary> Mod metadata as Penumbra (or the mod's meta.json) reports it. </summary>
public sealed record ModMeta(
    string Name,
    string Author,
    string Description,
    string Version,
    string Website,
    /// <summary> Absolute image path, or empty. </summary>
    string ImagePath,
    IReadOnlyList<string> Tags,
    /// <summary> Absolute mod folder, or empty when unknown. </summary>
    string ModPath);

/// <summary> One mod's settings inside one collection. </summary>
public sealed record ModSettingsInCollection(
    bool Enabled,
    int Priority,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Settings,
    bool Inherited,
    bool Temporary);

/// <summary>
/// Everything Vitrine reads from Penumbra, behind an interface so the catalog and design store are testable with a fake.
/// All calls are safe to make when Penumbra is unavailable: they return empty results instead of throwing.
/// </summary>
public interface IPenumbraReader : IDisposable
{
    /// <summary> True when Penumbra answered the version probe and its breaking version matches the bundled API. </summary>
    bool Available { get; }

    /// <summary> (Breaking, Feature) version, or null when unavailable. </summary>
    (int Breaking, int Feature)? ApiVersion { get; }

    /// <summary> Raised (on the framework thread) whenever Available flips. </summary>
    event Action? AvailabilityChanged;

    /// <summary> Re-checks availability and the API version now. </summary>
    void Probe();

    /// <summary> Penumbra's mod root directory, or empty. </summary>
    string GetModRootDirectory();

    /// <summary> Mod directory → display name. </summary>
    IReadOnlyDictionary<string, string> GetModList();

    /// <summary> Metadata from Penumbra's mod manager adapter, or null when the adapter is unavailable or the mod is unknown. </summary>
    ModMeta? GetModMeta(string modDirectory, string modName);

    /// <summary> Changed item display name → attached game object (or null). </summary>
    IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory);

    /// <summary> Option group → (options, group type as Penumbra's GroupType int). Null when the mod is unknown. </summary>
    IReadOnlyDictionary<string, (string[] Options, int GroupType)>? GetAvailableModSettings(string modDirectory);

    /// <summary> Collection id → name. </summary>
    IReadOnlyDictionary<Guid, string> GetCollections();

    /// <summary> Every mod's settings in a collection (mod directory → settings). Null when the collection is unknown. </summary>
    IReadOnlyDictionary<string, ModSettingsInCollection>? GetAllModSettings(Guid collectionId);

    /// <summary> The effective collection of a game object (0 = local player). Null when the object is invalid. </summary>
    (Guid Id, string Name, bool IndividualSet)? GetCollectionForObject(int objectIndex);

    /// <summary> Which collection a special assignment (Default, Interface, Yourself, …) points at. Null when unassigned. </summary>
    (Guid Id, string Name)? GetSpecialCollection(global::Penumbra.Api.Enums.ApiCollectionType type);

    /// <summary> Writes a temporary mod setting (used for the "preview on character" button). Returns Penumbra's return code as int. </summary>
    int SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings, string source, int key);

    /// <summary> Removes a temporary mod setting written with <paramref name="key"/>. </summary>
    int RemoveTemporaryModSettings(Guid collectionId, string modDirectory, int key);

    /// <summary> Redraws a game object so temporary setting changes become visible. 0 = local player. </summary>
    void RedrawObject(int objectIndex);

    /// <summary> Mod directory of a newly added mod. </summary>
    event Action<string>? ModAdded;

    /// <summary> Mod directory of a deleted mod. </summary>
    event Action<string>? ModDeleted;

    /// <summary> (old directory, new directory). </summary>
    event Action<string, string>? ModMoved;
}
