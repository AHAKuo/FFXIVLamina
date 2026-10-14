using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lamina.Runtime;
using Newtonsoft.Json.Linq;

namespace Lamina.Ipc;

/// <summary>
/// Honorific IPC bridge (TDD 11.3). Honorific exposes raw call gates (see Honorific/IpcProvider.cs), so this bridge uses
/// Dalamud's <see cref="ICallGateSubscriber{T1}"/> directly. Titles are passed as JSON matching Honorific's <c>TitleData</c>.
/// </summary>
public sealed class HonorificBridge : BridgeBase, IHonorificBridge
{
    /// <summary> IPC namespace used by Honorific. </summary>
    public const string NameSpace = "Honorific";

    /// <summary> Honorific's major API version this bridge was written against (see Honorific/IpcProvider.cs MajorVersion). </summary>
    public const int RequiredMajorVersion = 3;

    private const string ApiVersionLabel          = NameSpace + ".ApiVersion";
    private const string SetCharacterTitleLabel   = NameSpace + ".SetCharacterTitle";
    private const string ClearCharacterTitleLabel = NameSpace + ".ClearCharacterTitle";
    private const string GetCharacterTitleLabel   = NameSpace + ".GetCharacterTitle";
    private const string ReadyLabel               = NameSpace + ".Ready";
    private const string DisposingLabel           = NameSpace + ".Disposing";

    private readonly ICallGateSubscriber<(uint, uint)>      _apiVersion;
    private readonly ICallGateSubscriber<int, string, object> _setCharacterTitle;
    private readonly ICallGateSubscriber<int, object>         _clearCharacterTitle;
    private readonly ICallGateSubscriber<int, string>         _getCharacterTitle;
    private readonly ICallGateSubscriber<object>              _ready;
    private readonly ICallGateSubscriber<object>              _disposing;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public HonorificBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Honorific", RequiredMajorVersion)
    {
        _apiVersion          = pi.GetIpcSubscriber<(uint, uint)>(ApiVersionLabel);
        _setCharacterTitle   = pi.GetIpcSubscriber<int, string, object>(SetCharacterTitleLabel);
        _clearCharacterTitle = pi.GetIpcSubscriber<int, object>(ClearCharacterTitleLabel);
        _getCharacterTitle   = pi.GetIpcSubscriber<int, string>(GetCharacterTitleLabel);
        _ready               = pi.GetIpcSubscriber<object>(ReadyLabel);
        _disposing           = pi.GetIpcSubscriber<object>(DisposingLabel);

        _ready.Subscribe(Probe);
        _disposing.Subscribe(Probe);

        Probe();
    }

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
    {
        var (major, minor) = _apiVersion.InvokeFunc();
        return ((int)major, (int)minor);
    }

    /// <inheritdoc/>
    public bool SetTitle(int objectIndex, HonorificTitle? title)
    {
        if (title is null || title.Clear || string.IsNullOrEmpty(title.Title))
            return Call(() => _clearCharacterTitle.InvokeAction(objectIndex), "ClearCharacterTitle");

        var json = BuildTitleJson(title);
        return Call(() => _setCharacterTitle.InvokeAction(objectIndex, json), "SetCharacterTitle");
    }

    /// <inheritdoc/>
    public string GetTitle(int objectIndex)
        => Call(() => _getCharacterTitle.InvokeFunc(objectIndex) ?? string.Empty, string.Empty, "GetCharacterTitle");

    /// <summary>
    /// Serializes a title the way Honorific's <c>TitleData</c> expects it: <c>Title</c>, <c>IsPrefix</c>, and optional
    /// <c>Color</c> / <c>Glow</c> as <c>{X, Y, Z}</c> objects (Newtonsoft's default layout for <see cref="System.Numerics.Vector3"/>).
    /// </summary>
    public static string BuildTitleJson(HonorificTitle title)
    {
        var obj = new JObject
        {
            ["Title"]    = title.Title ?? string.Empty,
            ["IsPrefix"] = title.IsPrefix,
        };
        if (title.Color is { } color)
            obj["Color"] = Vector(color);
        if (title.Glow is { } glow)
            obj["Glow"] = Vector(glow);
        return obj.ToString(Newtonsoft.Json.Formatting.None);

        static JObject Vector(System.Numerics.Vector3 v)
            => new()
            {
                ["X"] = v.X,
                ["Y"] = v.Y,
                ["Z"] = v.Z,
            };
    }

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _ready.Unsubscribe(Probe);
        _disposing.Unsubscribe(Probe);
    }
}
