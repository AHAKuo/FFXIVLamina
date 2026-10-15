using Lamina.Ipc;

namespace Lamina.Tests.Fakes;

/// <summary> In-memory Vitrine bridge: a settable design list, a call log, and a manual DesignsChanged trigger. </summary>
public sealed class FakeVitrineBridge : FakeBridgeBase, IVitrineBridge
{
    public FakeVitrineBridge()
        : base("Vitrine")
    { }

    /// <summary> Designs the fake publishes. </summary>
    public List<VitrineDesignInfo> DesignList { get; } = new();

    /// <summary> Message returned by <see cref="PreviewDesign"/>. </summary>
    public string PreviewMessage { get; set; } = "Preview written.";

    /// <summary> Last argument passed to <see cref="OpenWindow"/>. </summary>
    public bool? LastOpenWindow { get; private set; }

    public IReadOnlyList<VitrineDesignInfo> Designs
        => Available ? DesignList : Array.Empty<VitrineDesignInfo>();

    public event Action? DesignsChanged;

    public VitrineDesignInfo? GetDesign(Guid id)
        => Available ? DesignList.FirstOrDefault(d => d.Id == id) : null;

    public void OpenWindow(bool? open)
    {
        Record(nameof(OpenWindow), open);
        LastOpenWindow = open;
    }

    public string PreviewDesign(Guid id)
    {
        Record(nameof(PreviewDesign), id);
        return Available ? PreviewMessage : "Vitrine is not installed.";
    }

    public void RefreshDesigns()
    {
        Record(nameof(RefreshDesigns));
        DesignsChanged?.Invoke();
    }

    /// <summary> Simulates Vitrine reporting changed designs. </summary>
    public void RaiseDesignsChanged()
        => DesignsChanged?.Invoke();
}
