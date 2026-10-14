using Glamourer.Api.Enums;
using Lamina.Ipc;
using Newtonsoft.Json.Linq;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Glamourer bridge. Designs and states are dictionaries the test fills. </summary>
public sealed class FakeGlamourerBridge : FakeBridgeBase, IGlamourerBridge, IGlamourerUnlock
{
    public FakeGlamourerBridge()
        : base("Glamourer")
    { }

    /// <summary> Design id → (display name, full path, color). </summary>
    public Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> Designs { get; } = new();

    /// <summary> Design id → raw design JObject returned by <see cref="GetDesign"/>. </summary>
    public Dictionary<Guid, JObject> DesignObjects { get; } = new();

    /// <summary> Object index → current state returned by <see cref="GetState"/>. </summary>
    public Dictionary<int, JObject> States { get; } = new();

    /// <summary> Object index → the last state applied through <see cref="ApplyState"/>. </summary>
    public Dictionary<int, (JObject State, bool Locked)> Applied { get; } = new();

    /// <summary> Result returned by <see cref="ApplyState"/>. </summary>
    public GlamourerApiEc ApplyResult { get; set; } = GlamourerApiEc.Success;

    /// <summary> Result returned by <see cref="UnlockAndRevertToAutomation"/>. </summary>
    public GlamourerApiEc RevertResult { get; set; } = GlamourerApiEc.Success;

    public event Action<nint, StateFinalizationType>? StateFinalized;

    /// <summary> Adds a design with a name and its JObject in one go. </summary>
    public Guid AddDesign(string name, JObject design, string? path = null, uint color = 0)
    {
        var id = Guid.NewGuid();
        Designs[id]       = (name, path ?? name, color);
        DesignObjects[id] = design;
        return id;
    }

    public IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> GetDesigns()
    {
        Record(nameof(GetDesigns));
        return Available ? new Dictionary<Guid, (string, string, uint)>(Designs) : new Dictionary<Guid, (string, string, uint)>();
    }

    public JObject? GetDesign(Guid designId)
    {
        Record(nameof(GetDesign), designId);
        return Available && DesignObjects.TryGetValue(designId, out var design) ? (JObject)design.DeepClone() : null;
    }

    public (GlamourerApiEc Code, JObject? State) GetState(int objectIndex)
    {
        Record(nameof(GetState), objectIndex);
        if (!Available)
            return (GlamourerApiEc.UnknownError, null);

        return States.TryGetValue(objectIndex, out var state)
            ? (GlamourerApiEc.Success, (JObject)state.DeepClone())
            : (GlamourerApiEc.ActorNotFound, null);
    }

    public GlamourerApiEc ApplyState(JObject state, int objectIndex, bool lockState)
    {
        Record(nameof(ApplyState), state, objectIndex, lockState);
        if (!Available)
            return GlamourerApiEc.UnknownError;

        Applied[objectIndex] = ((JObject)state.DeepClone(), lockState);
        return ApplyResult;
    }

    public GlamourerApiEc Unlock(int objectIndex)
    {
        Record(nameof(Unlock), objectIndex);
        if (!Available)
            return GlamourerApiEc.UnknownError;

        if (Applied.TryGetValue(objectIndex, out var current))
            Applied[objectIndex] = (current.State, false);
        return GlamourerApiEc.Success;
    }

    /// <summary> Items set through <see cref="SetItem"/>: (objectIndex, slot) to item id. </summary>
    public Dictionary<(int Index, ApiEquipSlot Slot), ulong> Items { get; } = new();

    public GlamourerApiEc SetItem(int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains)
    {
        Record(nameof(SetItem), objectIndex, slot, itemId, stains);
        if (!Available)
            return GlamourerApiEc.UnknownError;

        Items[(objectIndex, slot)] = itemId;
        return ApplyResult;
    }

    public GlamourerApiEc UnlockAndRevertToAutomation(int objectIndex)
    {
        Record(nameof(UnlockAndRevertToAutomation), objectIndex);
        if (!Available)
            return GlamourerApiEc.UnknownError;

        Applied.Remove(objectIndex);
        return RevertResult;
    }

    /// <summary> Simulates Glamourer finalizing a state on an actor. </summary>
    public void RaiseStateFinalized(nint address, StateFinalizationType type)
        => StateFinalized?.Invoke(address, type);
}
