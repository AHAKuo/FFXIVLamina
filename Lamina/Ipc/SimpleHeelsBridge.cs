using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace Lamina.Ipc;

/// <summary>
/// Simple Heels IPC bridge. Simple Heels exposes raw call gates named <c>SimpleHeels.&lt;Name&gt;</c> (see
/// SimpleHeels/SimpleHeels/ApiProvider.cs). A configuration registered for an actor with <c>RegisterPlayer</c> replaces that
/// actor's own Simple Heels configuration (per-footwear offsets included) until <c>UnregisterPlayer</c>; Lamina registers
/// just a <c>DefaultOffset</c>. The local player's own resolved configuration keeps arriving through <c>GetLocalPlayer</c> /
/// <c>LocalChanged</c>, which Simple Heels builds with IPC registrations excluded, so a relative offset can follow it.
/// </summary>
public sealed class SimpleHeelsBridge : BridgeBase, ISimpleHeelsBridge
{
    /// <summary> IPC namespace used by Simple Heels. </summary>
    public const string NameSpace = "SimpleHeels";

    /// <summary> Simple Heels' major API version this bridge was written against (ApiProvider.ApiVersionMajor). </summary>
    public const int RequiredMajorVersion = 2;

    private const string ApiVersionLabel       = NameSpace + ".ApiVersion";
    private const string GetLocalPlayerLabel   = NameSpace + ".GetLocalPlayer";
    private const string LocalChangedLabel     = NameSpace + ".LocalChanged";
    private const string RegisterPlayerLabel   = NameSpace + ".RegisterPlayer";
    private const string UnregisterPlayerLabel = NameSpace + ".UnregisterPlayer";
    private const string ReadyLabel            = NameSpace + ".Ready";

    private readonly ICallGateSubscriber<(int, int)>          _apiVersion;
    private readonly ICallGateSubscriber<string>              _getLocalPlayer;
    private readonly ICallGateSubscriber<string, object>      _localChanged;
    private readonly ICallGateSubscriber<int, string, object> _registerPlayer;
    private readonly ICallGateSubscriber<int, object>         _unregisterPlayer;
    private readonly ICallGateSubscriber<object>              _ready;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public SimpleHeelsBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Simple Heels", RequiredMajorVersion)
    {
        _apiVersion       = pi.GetIpcSubscriber<(int, int)>(ApiVersionLabel);
        _getLocalPlayer   = pi.GetIpcSubscriber<string>(GetLocalPlayerLabel);
        _localChanged     = pi.GetIpcSubscriber<string, object>(LocalChangedLabel);
        _registerPlayer   = pi.GetIpcSubscriber<int, string, object>(RegisterPlayerLabel);
        _unregisterPlayer = pi.GetIpcSubscriber<int, object>(UnregisterPlayerLabel);
        _ready            = pi.GetIpcSubscriber<object>(ReadyLabel);

        _localChanged.Subscribe(OnLocalChanged);
        _ready.Subscribe(Probe);

        Probe();
    }

    /// <inheritdoc/>
    public event Action<float>? LocalOffsetChanged;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
        => _apiVersion.InvokeFunc();

    /// <inheritdoc/>
    public bool RegisterOffset(int objectIndex, float offset)
        => Call(() => _registerPlayer.InvokeAction(objectIndex, BuildConfigJson(offset)), "RegisterPlayer");

    /// <inheritdoc/>
    public bool Unregister(int objectIndex)
        => Call(() => _unregisterPlayer.InvokeAction(objectIndex), "UnregisterPlayer");

    /// <inheritdoc/>
    public float? GetLocalOffset()
        => Call<float?>(() => ParseOffset(_getLocalPlayer.InvokeFunc()), null, "GetLocalPlayer");

    /// <summary> The JSON Simple Heels' <c>IpcCharacterConfig</c> reads: only the fixed height offset is set. </summary>
    public static string BuildConfigJson(float offset)
        => new JObject { ["DefaultOffset"] = offset }.ToString(Newtonsoft.Json.Formatting.None);

    /// <summary> Reads <c>DefaultOffset</c> from an <c>IpcCharacterConfig</c> JSON; 0 when absent (Simple Heels omits a zero offset), null when unparsable. </summary>
    public static float? ParseOffset(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0f;

        try
        {
            var obj = JObject.Parse(json);
            var token = obj["DefaultOffset"];
            if (token is null || token.Type == JTokenType.Null)
                return 0f;
            return token.Type == JTokenType.String
                ? float.Parse(token.Value<string>()!, CultureInfo.InvariantCulture)
                : token.Value<float>();
        }
        catch
        {
            return null;
        }
    }

    private void OnLocalChanged(string json)
    {
        if (ParseOffset(json) is { } offset)
            Dispatch(() => LocalOffsetChanged?.Invoke(offset), nameof(LocalOffsetChanged));
    }

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _localChanged.Unsubscribe(OnLocalChanged);
        _ready.Unsubscribe(Probe);
        LocalOffsetChanged = null;
    }
}
