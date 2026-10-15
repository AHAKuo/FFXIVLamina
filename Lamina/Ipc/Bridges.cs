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
