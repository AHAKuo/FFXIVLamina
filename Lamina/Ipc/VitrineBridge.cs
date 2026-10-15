using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace Lamina.Ipc;

/// <summary>
/// Vitrine IPC bridge. Vitrine is a separate, optional plugin; it publishes its designs as JSON over the call gates
/// documented in the Vitrine repository (docs/IPC.md). The design list is cached here and refreshed whenever Vitrine
/// reports a change or becomes available, so the compositor and the UI never pay an IPC call per lookup.
/// </summary>
public sealed class VitrineBridge : BridgeBase, IVitrineBridge
{
    /// <summary> IPC namespace used by Vitrine. </summary>
    public const string NameSpace = "Vitrine";

    /// <summary> Vitrine's major API version this bridge was written against. </summary>
    public const int RequiredMajorVersion = 1;

    private const string ApiVersionLabel     = NameSpace + ".ApiVersion";
    private const string ReadyLabel          = NameSpace + ".Ready";
    private const string DisposedLabel       = NameSpace + ".Disposed";
    private const string GetDesignsLabel     = NameSpace + ".GetDesigns";
    private const string DesignsChangedLabel = NameSpace + ".DesignsChanged";
    private const string OpenWindowLabel     = NameSpace + ".OpenWindow";
    private const string PreviewDesignLabel  = NameSpace + ".PreviewDesign";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling     = NullValueHandling.Ignore,
    };

    private readonly ICallGateSubscriber<(int, int)>     _apiVersion;
    private readonly ICallGateSubscriber<object>         _ready;
    private readonly ICallGateSubscriber<object>         _disposed;
    private readonly ICallGateSubscriber<string>         _getDesigns;
    private readonly ICallGateSubscriber<object>         _designsChanged;
    private readonly ICallGateSubscriber<bool?, object>  _openWindow;
    private readonly ICallGateSubscriber<Guid, string>   _previewDesign;

    private readonly object _cacheLock = new();
    private IReadOnlyList<VitrineDesignInfo>         _designs   = Array.Empty<VitrineDesignInfo>();
    private IReadOnlyDictionary<Guid, VitrineDesignInfo> _byId   = new Dictionary<Guid, VitrineDesignInfo>();

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public VitrineBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Vitrine", RequiredMajorVersion)
    {
        _apiVersion     = pi.GetIpcSubscriber<(int, int)>(ApiVersionLabel);
        _ready          = pi.GetIpcSubscriber<object>(ReadyLabel);
        _disposed       = pi.GetIpcSubscriber<object>(DisposedLabel);
        _getDesigns     = pi.GetIpcSubscriber<string>(GetDesignsLabel);
        _designsChanged = pi.GetIpcSubscriber<object>(DesignsChangedLabel);
        _openWindow     = pi.GetIpcSubscriber<bool?, object>(OpenWindowLabel);
        _previewDesign  = pi.GetIpcSubscriber<Guid, string>(PreviewDesignLabel);

        _ready.Subscribe(Probe);
        _disposed.Subscribe(Probe);
        _designsChanged.Subscribe(OnDesignsChanged);
        AvailabilityChanged += OnAvailabilityChanged;

        Probe();
        RefreshDesigns();
    }

    /// <inheritdoc/>
    public IReadOnlyList<VitrineDesignInfo> Designs
        => _designs;

    /// <inheritdoc/>
    public event Action? DesignsChanged;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
        => _apiVersion.InvokeFunc();

    /// <inheritdoc/>
    public VitrineDesignInfo? GetDesign(Guid id)
        => _byId.TryGetValue(id, out var design) ? design : null;

    /// <inheritdoc/>
    public void OpenWindow(bool? open)
        => Call(() => _openWindow.InvokeAction(open), "OpenWindow");

    /// <inheritdoc/>
    public string PreviewDesign(Guid id)
        => Available
            ? Call(() => _previewDesign.InvokeFunc(id) ?? string.Empty, "Vitrine did not answer.", "PreviewDesign")
            : "Vitrine is not installed.";

    /// <inheritdoc/>
    public void RefreshDesigns()
    {
        var json = Call(() => _getDesigns.InvokeFunc(), string.Empty, "GetDesigns");
        List<VitrineDesignInfo> parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(json) || !Available
                ? new List<VitrineDesignInfo>()
                : ParseDesigns(json);
        }
        catch (Exception e)
        {
            Log.Warning($"[{PluginName}] could not parse the design list: {e.GetType().Name}: {e.Message}");
            parsed = new List<VitrineDesignInfo>();
        }

        lock (_cacheLock)
        {
            _designs = parsed;
            var byId = new Dictionary<Guid, VitrineDesignInfo>(parsed.Count);
            foreach (var design in parsed)
                byId[design.Id] = design;
            _byId = byId;
        }

        Dispatch(() => DesignsChanged?.Invoke(), nameof(DesignsChanged));
    }

    /// <summary> Parses the JSON array Vitrine publishes. Designs without a valid id are dropped. </summary>
    public static List<VitrineDesignInfo> ParseDesigns(string json)
    {
        var list = JsonConvert.DeserializeObject<List<VitrineDesignInfo>>(json, JsonSettings) ?? new List<VitrineDesignInfo>();
        list.RemoveAll(d => d.Id == Guid.Empty);
        foreach (var design in list)
            design.Mods.RemoveAll(m => string.IsNullOrWhiteSpace(m.ModDirectory));
        return list;
    }

    /// <summary> Parses one design object as published by <c>Vitrine.GetDesign</c>. Null for an empty payload. </summary>
    public static VitrineDesignInfo? ParseDesign(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var design = JsonConvert.DeserializeObject<VitrineDesignInfo>(json, JsonSettings);
        return design is null || design.Id == Guid.Empty ? null : design;
    }

    private void OnDesignsChanged()
        => Dispatch(RefreshDesigns, nameof(OnDesignsChanged));

    private void OnAvailabilityChanged()
        => RefreshDesigns();

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        AvailabilityChanged -= OnAvailabilityChanged;
        _ready.Unsubscribe(Probe);
        _disposed.Unsubscribe(Probe);
        _designsChanged.Unsubscribe(OnDesignsChanged);
    }
}
