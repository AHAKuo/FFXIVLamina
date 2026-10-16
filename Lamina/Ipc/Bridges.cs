using System.Threading.Tasks;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Lamina.Runtime;

namespace Lamina.Ipc;

/// <summary> Shared constants for every bridge. </summary>
public static class IpcConstants
{
    /// <summary> Source string shown in Penumbra for Lamina's temporary settings. </summary>
    public const string PenumbraSource = "Lamina";

    /// <summary>
    /// Penumbra temporary-settings key. Negative = identification lock: the user can still edit, but only Lamina can remove
    /// all of its own settings in one call with the same key. ASCII "LAMI" negated.
    /// </summary>
    public const int PenumbraKey = -0x4C414D49;

    /// <summary> Glamourer lock key. Non-zero so Lamina's state is held against automation. ASCII "LAMI". </summary>
    public const uint GlamourerKey = 0x4C414D49;
}

/// <summary> Common availability surface. Bridges never throw on a missing plugin: calls become no-ops with a failure code. </summary>
public interface IPluginBridge
{
    string PluginName { get; }

    bool Available { get; }

    /// <summary> Version reported by the plugin, or null when unavailable. </summary>
    (int Major, int Minor)? ApiVersion { get; }

    /// <summary> Raised on the framework thread whenever Available flips. </summary>
    event Action? AvailabilityChanged;
}

public interface IGlamourerBridge : IPluginBridge
{
    /// <summary> Design id → (display name, full path in Glamourer's folders, display color). </summary>
    IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> GetDesigns();

    /// <summary> Raw design JObject as Glamourer serializes it (FileVersion 3 layout). Null if missing. Cached per call site is the caller's job. </summary>
    JObject? GetDesign(Guid designId);

    /// <summary> Current state of the actor in the same JObject layout. </summary>
    (GlamourerApiEc Code, JObject? State) GetState(int objectIndex);

    /// <summary> Applies a state JObject. Equipment + Customization flags always; Lock when <paramref name="lockState"/>. Never applies mod associations (Lamina does that). </summary>
    GlamourerApiEc ApplyState(JObject state, int objectIndex, bool lockState);

    /// <summary> Unlocks Lamina's lock (if any) and reverts to whatever automation would show. </summary>
    GlamourerApiEc UnlockAndRevertToAutomation(int objectIndex);

    /// <summary> Equips one item into a slot on the actor (Glamourer SetItem), with Lamina's key so a locked state accepts it. </summary>
    GlamourerApiEc SetItem(int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains);

    /// <summary> Raised when Glamourer finalizes a state on an actor (address, type). Used to re-assert after automation reapplies. </summary>
    event Action<nint, StateFinalizationType>? StateFinalized;

    /// <summary> Unlocks Lamina's lock (if any) and reverts to the plain game state: what the game itself has equipped / customized. </summary>
    GlamourerApiEc UnlockAndRevertToGame(int objectIndex);

    /// <summary>
    /// Creates a design inside Glamourer from a design JObject (FileVersion 3 layout, see <c>GlamourerDesignWriter</c>) and
    /// writes it to disk. Returns the result code (CouldNotParse when Glamourer rejects the JSON) and the new design's id
    /// (Guid.Empty on failure).
    /// </summary>
    (GlamourerApiEc Code, Guid Id) AddDesign(JObject design, string name);
}

public interface IPenumbraBridge : IPluginBridge
{
    IReadOnlyDictionary<Guid, string> GetCollections();

    /// <summary> Finds a collection by exact name (case-insensitive). </summary>
    Guid? FindCollection(string name);

    (bool ObjectValid, bool IndividualSet, Guid CollectionId, string CollectionName) GetCollectionForObject(int objectIndex);

    /// <summary> Writes one mod's temporary state into a collection with Lamina's source and key. </summary>
    PenumbraApiEc SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings);

    /// <summary> Clean slate: removes every temporary setting Lamina wrote into the collection, in one call. </summary>
    PenumbraApiEc RemoveAllTemporaryModSettings(Guid collectionId);

    void RedrawObject(int objectIndex);

    /// <summary> Mod directory → mod name. </summary>
    IReadOnlyDictionary<string, string> GetModList();

    /// <summary> Option group name → (option names, group type). Null when the mod is missing. </summary>
    IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? GetAvailableModSettings(string modDirectory);

    /// <summary> Changed item display name → game object (or null). </summary>
    IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory);

    /// <summary>
    /// Current settings of a mod in a collection: (enabled, priority, settings, inherited, temporary). Null when the mod is
    /// missing or not configured in the collection. With <paramref name="ignoreTemporary"/> the permanent settings are
    /// returned even while temporary ones (Lamina's or another plugin's) are active.
    /// </summary>
    (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings, bool Inherited, bool Temporary)? GetCurrentModSettings(Guid collectionId, string modDirectory, bool ignoreTemporary = false);

    // ---- decal support (resource trees, path resolution, temporary mods, texture conversion) ----

    /// <summary> The resource tree Penumbra sees for a drawn object (models → materials → textures, with actual disk paths). Null when unavailable. </summary>
    Penumbra.Api.ResourceTreeDto? GetGameObjectResourceTree(int objectIndex, bool withUiData = true);

    /// <summary> The file a game path resolves to for an object (through its collection, temporary mods included). The game path itself when unmodified. </summary>
    string ResolveGameObjectPath(string gamePath, int objectIndex);

    /// <summary> Adds (or replaces, same tag and priority) a temporary mod: game path → absolute file path redirects. Gone when Penumbra restarts. </summary>
    PenumbraApiEc AddTemporaryMod(string tag, Guid collectionId, IReadOnlyDictionary<string, string> redirects, int priority);

    /// <summary> Removes a temporary mod added with <see cref="AddTemporaryMod"/>. </summary>
    PenumbraApiEc RemoveTemporaryMod(string tag, Guid collectionId, int priority);

    /// <summary> Converts a .tex / .dds / .png file to another format through Penumbra's converter. </summary>
    Task ConvertTextureFile(string inputFile, string outputFile, TextureType textureType, bool mipMaps);

    /// <summary> Writes RGBA32 pixel data (row major, height = length / width / 4) as a texture file through Penumbra's converter. </summary>
    Task ConvertTextureData(byte[] rgbaData, int width, string outputFile, TextureType textureType, bool mipMaps);

    event Action<string>? ModAdded;
    event Action<string>? ModDeleted;
    event Action<string, string>? ModMoved;
    event Action<nint, int>? GameObjectRedrawn;
}

public interface IHonorificBridge : IPluginBridge
{
    /// <summary> Sets or clears the title on the actor. Clear when <paramref name="title"/> is null or title.Clear. </summary>
    bool SetTitle(int objectIndex, HonorificTitle? title);

    /// <summary> Title JSON currently shown on the actor, empty when none. </summary>
    string GetTitle(int objectIndex);
}

/// <summary> Result codes of the Customize+ IPC (see CustomizePlus/Api/Enums/ErrorCode.cs). </summary>
public enum CustomizePlusEc
{
    Success          = 0,
    InvalidCharacter = 1,
    CorruptedProfile = 2,
    ProfileNotFound  = 3,
    InvalidArgument  = 4,
    UnknownError     = 255,
}

/// <summary> One saved Customize+ profile as its list call reports it. </summary>
public sealed record CustomizePlusProfileInfo(Guid Id, string Name, string Path, int Priority, bool Enabled);

public interface ICustomizePlusBridge : IPluginBridge
{
    /// <summary> Every saved (non-temporary) profile. Heavy on the Customize+ side; cache it (see <c>CustomizePlusProfileCache</c>). </summary>
    IReadOnlyList<CustomizePlusProfileInfo> GetProfiles();

    /// <summary> The bone edits of a saved profile (its enabled templates merged), or null when the profile is missing or Customize+ is unavailable. </summary>
    IReadOnlyDictionary<string, CustomizePlusBone>? GetProfileBones(Guid profileId);

    /// <summary> Applies the bones as the actor's temporary profile (replacing an earlier one). Returns the code and the temporary profile's id. </summary>
    (CustomizePlusEc Code, Guid? TemporaryId) SetTemporaryProfile(int objectIndex, IReadOnlyDictionary<string, CustomizePlusBone> bones);

    /// <summary> Removes the actor's temporary profile. ProfileNotFound when there is none. </summary>
    CustomizePlusEc DeleteTemporaryProfile(int objectIndex);

    /// <summary> Raised on the framework thread when Customize+ reports a change to the profile active on an actor (saved profiles only). </summary>
    event Action? ProfilesChanged;
}

public interface ISimpleHeelsBridge : IPluginBridge
{
    /// <summary> Registers a configuration for the actor with just this height offset; it replaces the actor's own Simple Heels configuration while registered. </summary>
    bool RegisterOffset(int objectIndex, float offset);

    /// <summary> Removes the registered configuration so the actor's own Simple Heels configuration applies again. </summary>
    bool Unregister(int objectIndex);

    /// <summary> The local player's own resolved height offset (per-footwear configuration, IPC registrations excluded), or null when unavailable. </summary>
    float? GetLocalOffset();

    /// <summary> Raised on the framework thread with the new value of <see cref="GetLocalOffset"/> when Simple Heels reports a change to the local player. </summary>
    event Action<float>? LocalOffsetChanged;
}

public interface IMoodlesBridge : IPluginBridge
{
    /// <summary> Every saved Moodle: id, icon, full path, title. </summary>
    IReadOnlyList<(Guid Id, uint IconId, string FullPath, string Title)> GetMoodles();

    /// <summary> Every saved preset: id, full path. </summary>
    IReadOnlyList<(Guid Id, string FullPath)> GetPresets();

    void AddMoodle(nint actorAddress, Guid moodleId);

    void ApplyPreset(nint actorAddress, Guid presetId);

    void RemoveMoodles(nint actorAddress, IReadOnlyList<Guid> moodleIds);

    /// <summary> Removes every status a preset applied. </summary>
    void RemovePreset(nint actorAddress, Guid presetId);
}
