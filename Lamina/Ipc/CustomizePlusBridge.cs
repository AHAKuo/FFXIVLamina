using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lamina.Runtime;
using Newtonsoft.Json.Linq;

namespace Lamina.Ipc;

/// <summary>
/// Customize+ IPC bridge. Customize+ registers its call gates through EzIPC, so every label is
/// <c>CustomizePlus.&lt;Group&gt;.&lt;Method&gt;</c> (see CustomizePlus/CustomizePlus/Api/CustomizePlusIpc.*.cs). Profiles travel
/// as the JSON of its <c>IPCCharacterProfile</c>: <c>{ "Bones": { name: { Translation, Rotation, Scaling, ChildScaling, flags } } }</c>,
/// with Newtonsoft's default <c>{X, Y, Z}</c> layout for vectors and default-valued members omitted.
/// Lamina only ever sets and deletes <em>temporary</em> profiles, which Customize+ never persists.
/// </summary>
public sealed class CustomizePlusBridge : BridgeBase, ICustomizePlusBridge
{
    /// <summary> EzIPC prefix (Customize+'s internal plugin name). </summary>
    public const string Prefix = "CustomizePlus";

    /// <summary> Breaking API version this bridge was written against (<c>General.GetApiVersion</c> item 1). </summary>
    public const int RequiredBreakingVersion = 6;

    private const string ApiVersionLabel      = Prefix + ".General.GetApiVersion";
    private const string IsValidLabel         = Prefix + ".General.IsValid";
    private const string GetListLabel         = Prefix + ".Profile.GetList";
    private const string GetByUniqueIdLabel   = Prefix + ".Profile.GetByUniqueId";
    private const string SetTemporaryLabel    = Prefix + ".Profile.SetTemporaryProfileOnCharacter";
    private const string DeleteTemporaryLabel = Prefix + ".Profile.DeleteTemporaryProfileOnCharacter";
    private const string DeleteByIdLabel      = Prefix + ".Profile.DeleteTemporaryProfileByUniqueId";
    private const string OnUpdateLabel        = Prefix + ".Profile.OnUpdate";

    private readonly ICallGateSubscriber<(int, int)>                                                                     _apiVersion;
    private readonly ICallGateSubscriber<bool>                                                                           _isValid;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> _getList;
    private readonly ICallGateSubscriber<Guid, (int, string?)>                                                           _getByUniqueId;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)>                                                   _setTemporary;
    private readonly ICallGateSubscriber<ushort, int>                                                                    _deleteTemporary;
    private readonly ICallGateSubscriber<Guid, int>                                                                      _deleteById;
    private readonly ICallGateSubscriber<ushort, Guid, object>                                                           _onUpdate;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public CustomizePlusBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Customize+", RequiredBreakingVersion)
    {
        _apiVersion      = pi.GetIpcSubscriber<(int, int)>(ApiVersionLabel);
        _isValid         = pi.GetIpcSubscriber<bool>(IsValidLabel);
        _getList         = pi.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>(GetListLabel);
        _getByUniqueId   = pi.GetIpcSubscriber<Guid, (int, string?)>(GetByUniqueIdLabel);
        _setTemporary    = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>(SetTemporaryLabel);
        _deleteTemporary = pi.GetIpcSubscriber<ushort, int>(DeleteTemporaryLabel);
        _deleteById      = pi.GetIpcSubscriber<Guid, int>(DeleteByIdLabel);
        _onUpdate        = pi.GetIpcSubscriber<ushort, Guid, object>(OnUpdateLabel);

        _onUpdate.Subscribe(OnProfileUpdate);

        Probe();
    }

    /// <inheritdoc/>
    public event Action? ProfilesChanged;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
    {
        var (breaking, feature) = _apiVersion.InvokeFunc();
        // IsValid is false while Customize+ sits in a fatal error state; treat that like an absent plugin.
        if (!_isValid.InvokeFunc())
            throw new InvalidOperationException("Customize+ reports an invalid state.");
        return (breaking, feature);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CustomizePlusProfileInfo> GetProfiles()
        => Call<IReadOnlyList<CustomizePlusProfileInfo>>(() =>
        {
            var list = _getList.InvokeFunc();
            if (list is null)
                return [];
            var result = new List<CustomizePlusProfileInfo>(list.Count);
            foreach (var (id, name, path, _, priority, enabled) in list)
                result.Add(new CustomizePlusProfileInfo(id, name, path, priority, enabled));
            return result;
        }, [], "Profile.GetList");

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, CustomizePlusBone>? GetProfileBones(Guid profileId)
    {
        if (profileId == Guid.Empty)
            return null;

        return Call<IReadOnlyDictionary<string, CustomizePlusBone>?>(() =>
        {
            var (code, json) = _getByUniqueId.InvokeFunc(profileId);
            if (code != (int)CustomizePlusEc.Success || string.IsNullOrEmpty(json))
            {
                if (code != (int)CustomizePlusEc.ProfileNotFound)
                    Log.Debug($"[Customize+] GetByUniqueId({profileId}) returned {(CustomizePlusEc)code}.");
                return null;
            }

            return ParseBones(json);
        }, null, "Profile.GetByUniqueId");
    }

    /// <inheritdoc/>
    public (CustomizePlusEc Code, Guid? TemporaryId) SetTemporaryProfile(int objectIndex, IReadOnlyDictionary<string, CustomizePlusBone> bones)
    {
        if (objectIndex is < 0 or > ushort.MaxValue)
            return (CustomizePlusEc.InvalidCharacter, null);

        var json = BuildProfileJson(bones);
        return Call(() =>
        {
            var (code, id) = _setTemporary.InvokeFunc((ushort)objectIndex, json);
            return ((CustomizePlusEc)code, id);
        }, (CustomizePlusEc.UnknownError, (Guid?)null), "Profile.SetTemporaryProfileOnCharacter");
    }

    /// <inheritdoc/>
    public CustomizePlusEc DeleteTemporaryProfile(int objectIndex)
    {
        if (objectIndex is < 0 or > ushort.MaxValue)
            return CustomizePlusEc.InvalidCharacter;

        return Call(() => (CustomizePlusEc)_deleteTemporary.InvokeFunc((ushort)objectIndex), CustomizePlusEc.UnknownError, "Profile.DeleteTemporaryProfileOnCharacter");
    }

    /// <inheritdoc/>
    public CustomizePlusEc DeleteTemporaryProfileById(Guid temporaryId)
    {
        if (temporaryId == Guid.Empty)
            return CustomizePlusEc.ProfileNotFound;

        return Call(() => (CustomizePlusEc)_deleteById.InvokeFunc(temporaryId), CustomizePlusEc.UnknownError, "Profile.DeleteTemporaryProfileByUniqueId");
    }

    /// <summary> Parses an <c>IPCCharacterProfile</c> JSON into bones. Members Customize+ omitted (default values) read as zero / false. </summary>
    public static IReadOnlyDictionary<string, CustomizePlusBone> ParseBones(string json)
    {
        var result = new Dictionary<string, CustomizePlusBone>(StringComparer.Ordinal);
        if (JObject.Parse(json)["Bones"] is not JObject bones)
            return result;

        foreach (var (name, token) in bones)
        {
            if (token is not JObject bone || string.IsNullOrEmpty(name))
                continue;
            result[name] = new CustomizePlusBone(
                Vector(bone["Translation"]),
                Vector(bone["Rotation"]),
                Vector(bone["Scaling"]),
                Vector(bone["ChildScaling"]),
                bone.Value<bool?>("ChildScaleIndependent") ?? false,
                bone.Value<bool?>("PropagateTranslation") ?? false,
                bone.Value<bool?>("PropagateRotation") ?? false,
                bone.Value<bool?>("PropagateScale") ?? false);
        }

        return result;

        static Vector3 Vector(JToken? t)
            => t is JObject o ? new Vector3(o.Value<float?>("X") ?? 0f, o.Value<float?>("Y") ?? 0f, o.Value<float?>("Z") ?? 0f) : Vector3.Zero;
    }

    /// <summary> Serializes bones the way Customize+'s <c>IPCCharacterProfile</c> expects them; every member written explicitly. </summary>
    public static string BuildProfileJson(IReadOnlyDictionary<string, CustomizePlusBone> bones)
    {
        var obj = new JObject();
        foreach (var (name, bone) in bones.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            obj[name] = new JObject
            {
                ["Translation"]           = Vector(bone.Translation),
                ["Rotation"]              = Vector(bone.Rotation),
                ["Scaling"]               = Vector(bone.Scaling),
                ["ChildScaling"]          = Vector(bone.ChildScaling),
                ["ChildScaleIndependent"] = bone.ChildScaleIndependent,
                ["PropagateTranslation"]  = bone.PropagateTranslation,
                ["PropagateRotation"]     = bone.PropagateRotation,
                ["PropagateScale"]        = bone.PropagateScale,
            };
        return new JObject { ["Bones"] = obj }.ToString(Newtonsoft.Json.Formatting.None);

        static JObject Vector(Vector3 v)
            => new()
            {
                ["X"] = v.X,
                ["Y"] = v.Y,
                ["Z"] = v.Z,
            };
    }

    private void OnProfileUpdate(ushort objectIndex, Guid profileId)
        => Dispatch(() => ProfilesChanged?.Invoke(), nameof(ProfilesChanged));

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _onUpdate.Unsubscribe(OnProfileUpdate);
        ProfilesChanged = null;
    }
}
