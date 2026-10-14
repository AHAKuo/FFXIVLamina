using Lamina.Ipc;
using Lamina.Runtime;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Honorific bridge. Remembers the title per object index. </summary>
public sealed class FakeHonorificBridge : FakeBridgeBase, IHonorificBridge
{
    public FakeHonorificBridge()
        : base("Honorific")
    { }

    /// <summary> Object index → the title currently set (absent = cleared). </summary>
    public Dictionary<int, HonorificTitle> Titles { get; } = new();

    public bool SetTitle(int objectIndex, HonorificTitle? title)
    {
        Record(nameof(SetTitle), objectIndex, title);
        if (!Available)
            return false;

        if (title is null || title.Clear || string.IsNullOrEmpty(title.Title))
            Titles.Remove(objectIndex);
        else
            Titles[objectIndex] = title;
        return true;
    }

    public string GetTitle(int objectIndex)
    {
        Record(nameof(GetTitle), objectIndex);
        if (!Available || !Titles.TryGetValue(objectIndex, out var title))
            return string.Empty;

        return HonorificBridge.BuildTitleJson(title);
    }
}
