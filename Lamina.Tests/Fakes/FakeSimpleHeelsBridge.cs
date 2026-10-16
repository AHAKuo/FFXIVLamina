using Lamina.Ipc;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Simple Heels bridge: the registered offset per object index and a settable local offset. </summary>
public sealed class FakeSimpleHeelsBridge : FakeBridgeBase, ISimpleHeelsBridge
{
    public FakeSimpleHeelsBridge()
        : base("Simple Heels")
    { }

    /// <summary> Object index → registered height offset (absent = unregistered). </summary>
    public Dictionary<int, float> Registered { get; } = new();

    /// <summary> The local player's own resolved offset the fake reports. </summary>
    public float LocalOffset { get; set; }

    public event Action<float>? LocalOffsetChanged;

    public bool RegisterOffset(int objectIndex, float offset)
    {
        Record(nameof(RegisterOffset), objectIndex, offset);
        if (!Available)
            return false;
        Registered[objectIndex] = offset;
        return true;
    }

    public bool Unregister(int objectIndex)
    {
        Record(nameof(Unregister), objectIndex);
        if (!Available)
            return false;
        Registered.Remove(objectIndex);
        return true;
    }

    public float? GetLocalOffset()
    {
        Record(nameof(GetLocalOffset));
        return Available ? LocalOffset : null;
    }

    /// <summary> Simulates the player's own Simple Heels offset changing (footwear swap). </summary>
    public void ChangeLocalOffset(float offset)
    {
        LocalOffset = offset;
        LocalOffsetChanged?.Invoke(offset);
    }
}
