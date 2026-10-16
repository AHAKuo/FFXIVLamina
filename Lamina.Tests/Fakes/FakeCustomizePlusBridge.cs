using Lamina.Ipc;
using Lamina.Runtime;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Customize+ bridge: a settable profile list with bones, and the temporary profile per object index. </summary>
public sealed class FakeCustomizePlusBridge : FakeBridgeBase, ICustomizePlusBridge
{
    public FakeCustomizePlusBridge()
        : base("Customize+")
    { }

    /// <summary> Saved profiles the fake publishes. </summary>
    public List<CustomizePlusProfileInfo> ProfileList { get; } = new();

    /// <summary> Profile id → bones. </summary>
    public Dictionary<Guid, Dictionary<string, CustomizePlusBone>> Bones { get; } = new();

    /// <summary> Object index → (temporary profile id, bones) currently set. </summary>
    public Dictionary<int, (Guid Id, IReadOnlyDictionary<string, CustomizePlusBone> Bones)> Temporary { get; } = new();

    /// <summary> Code returned by <see cref="SetTemporaryProfile"/> when not Success (to simulate failures). </summary>
    public CustomizePlusEc SetResult { get; set; } = CustomizePlusEc.Success;

    public event Action? ProfilesChanged;

    /// <summary> Adds a profile with the given bones and returns its id. </summary>
    public Guid AddProfile(string name, Dictionary<string, CustomizePlusBone> bones, string? path = null, bool enabled = true)
    {
        var id = Guid.NewGuid();
        ProfileList.Add(new CustomizePlusProfileInfo(id, name, path ?? name, 0, enabled));
        Bones[id] = bones;
        return id;
    }

    public IReadOnlyList<CustomizePlusProfileInfo> GetProfiles()
    {
        Record(nameof(GetProfiles));
        return Available ? ProfileList.ToList() : Array.Empty<CustomizePlusProfileInfo>();
    }

    public IReadOnlyDictionary<string, CustomizePlusBone>? GetProfileBones(Guid profileId)
    {
        Record(nameof(GetProfileBones), profileId);
        if (!Available || !Bones.TryGetValue(profileId, out var bones))
            return null;
        return new Dictionary<string, CustomizePlusBone>(bones, StringComparer.Ordinal);
    }

    public (CustomizePlusEc Code, Guid? TemporaryId) SetTemporaryProfile(int objectIndex, IReadOnlyDictionary<string, CustomizePlusBone> bones)
    {
        Record(nameof(SetTemporaryProfile), objectIndex, bones.Count);
        if (!Available)
            return (CustomizePlusEc.UnknownError, null);
        if (SetResult != CustomizePlusEc.Success)
            return (SetResult, null);

        var id = Guid.NewGuid();
        Temporary[objectIndex] = (id, new Dictionary<string, CustomizePlusBone>(bones, StringComparer.Ordinal));
        return (CustomizePlusEc.Success, id);
    }

    public CustomizePlusEc DeleteTemporaryProfile(int objectIndex)
    {
        Record(nameof(DeleteTemporaryProfile), objectIndex);
        if (!Available)
            return CustomizePlusEc.UnknownError;
        return Temporary.Remove(objectIndex) ? CustomizePlusEc.Success : CustomizePlusEc.ProfileNotFound;
    }

    /// <summary> Simulates Customize+ reporting a profile change. </summary>
    public void RaiseProfilesChanged()
        => ProfilesChanged?.Invoke();
}
