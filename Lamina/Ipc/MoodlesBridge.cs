using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Lamina.Ipc;

/// <summary>
/// Optional capability: notification when an actor's Moodles status manager changed (a moodle was added, expired or removed
/// by anyone). Implemented by the real bridge and the test fake; the engine uses it to re-assert expired moodles.
/// </summary>
public interface IMoodlesStatusEvents
{
    /// <summary> Raised on the framework thread with the actor address whose status manager changed. </summary>
    event Action<nint>? StatusManagerModified;
}

/// <summary>
/// Moodles IPC bridge (TDD 11.4). Moodles registers its call gates through ECommons EzIPC, so every label is
/// <c>Moodles.&lt;MethodName&gt;</c> (see Moodles/Moodles/IPCProcessor.cs). Actors are addressed by pointer, except
/// preset removal, which Moodles only offers per <see cref="IPlayerCharacter"/>.
/// </summary>
public sealed class MoodlesBridge : BridgeBase, IMoodlesBridge, IMoodlesStatusEvents
{
    /// <summary> EzIPC prefix (Moodles' internal plugin name). </summary>
    public const string Prefix = "Moodles";

    /// <summary> Lowest <c>Moodles.Version</c> value the V2 call gates used here exist in. The bundled source reports 4. </summary>
    public const int MinimumVersion = 3;

    private const string VersionLabel               = Prefix + ".Version";
    private const string GetRegisteredMoodlesLabel  = Prefix + ".GetRegisteredMoodlesV2";
    private const string GetRegisteredProfilesLabel = Prefix + ".GetRegisteredProfilesV2";
    private const string AddOrUpdateMoodleLabel     = Prefix + ".AddOrUpdateMoodleByPtrV2";
    private const string ApplyPresetLabel           = Prefix + ".ApplyPresetByPtrV2";
    private const string RemoveMoodlesLabel         = Prefix + ".RemoveMoodlesByPtrV2";
    private const string RemovePresetLabel          = Prefix + ".RemovePresetByPlayerV2";
    private const string StatusManagerModifiedLabel = Prefix + ".StatusManagerModified";
    private const string ReadyLabel                 = Prefix + ".Ready";
    private const string UnloadingLabel             = Prefix + ".Unloading";

    private readonly IObjectTable _objects;
    private readonly ICallGateSubscriber<int>                                     _version;
    private readonly ICallGateSubscriber<List<(Guid, uint, string, string)>>      _getRegisteredMoodles;
    private readonly ICallGateSubscriber<List<(Guid, string)>>                    _getRegisteredProfiles;
    private readonly ICallGateSubscriber<Guid, nint, object>                      _addOrUpdateMoodle;
    private readonly ICallGateSubscriber<Guid, nint, object>                      _applyPreset;
    private readonly ICallGateSubscriber<List<Guid>, nint, object>                _removeMoodles;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object>          _removePreset;
    private readonly ICallGateSubscriber<nint, object>                            _statusManagerModified;
    private readonly ICallGateSubscriber<object>                                  _ready;
    private readonly ICallGateSubscriber<object>                                  _unloading;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    /// <param name="objects"> Object table, used to turn an actor address into the player character Moodles wants for preset removal. </param>
    public MoodlesBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework, IObjectTable objects)
        : base(pi, log, framework, "Moodles", requiredMajor: -1)
    {
        _objects               = objects;
        _version               = pi.GetIpcSubscriber<int>(VersionLabel);
        _getRegisteredMoodles  = pi.GetIpcSubscriber<List<(Guid, uint, string, string)>>(GetRegisteredMoodlesLabel);
        _getRegisteredProfiles = pi.GetIpcSubscriber<List<(Guid, string)>>(GetRegisteredProfilesLabel);
        _addOrUpdateMoodle     = pi.GetIpcSubscriber<Guid, nint, object>(AddOrUpdateMoodleLabel);
        _applyPreset           = pi.GetIpcSubscriber<Guid, nint, object>(ApplyPresetLabel);
        _removeMoodles         = pi.GetIpcSubscriber<List<Guid>, nint, object>(RemoveMoodlesLabel);
        _removePreset          = pi.GetIpcSubscriber<Guid, IPlayerCharacter, object>(RemovePresetLabel);
        _statusManagerModified = pi.GetIpcSubscriber<nint, object>(StatusManagerModifiedLabel);
        _ready                 = pi.GetIpcSubscriber<object>(ReadyLabel);
        _unloading             = pi.GetIpcSubscriber<object>(UnloadingLabel);

        _statusManagerModified.Subscribe(OnStatusManagerModified);
        _ready.Subscribe(Probe);
        _unloading.Subscribe(Probe);

        Probe();
    }

    /// <inheritdoc/>
    public event Action<nint>? StatusManagerModified;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
    {
        var version = _version.InvokeFunc();
        Log.Debug($"[Moodles] reported IPC version {version}.");
        return (version, 0);
    }

    /// <inheritdoc/>
    protected override bool VersionAccepted((int Major, int Minor) version)
        => version.Major >= MinimumVersion;

    /// <inheritdoc/>
    public IReadOnlyList<(Guid Id, uint IconId, string FullPath, string Title)> GetMoodles()
        => Call<IReadOnlyList<(Guid, uint, string, string)>>(() => _getRegisteredMoodles.InvokeFunc() ?? [], [], "GetRegisteredMoodlesV2");

    /// <inheritdoc/>
    public IReadOnlyList<(Guid Id, string FullPath)> GetPresets()
        => Call<IReadOnlyList<(Guid, string)>>(() => _getRegisteredProfiles.InvokeFunc() ?? [], [], "GetRegisteredProfilesV2");

    /// <inheritdoc/>
    public void AddMoodle(nint actorAddress, Guid moodleId)
    {
        if (actorAddress == nint.Zero || moodleId == Guid.Empty)
            return;

        Call(() => _addOrUpdateMoodle.InvokeAction(moodleId, actorAddress), "AddOrUpdateMoodleByPtrV2");
    }

    /// <inheritdoc/>
    public void ApplyPreset(nint actorAddress, Guid presetId)
    {
        if (actorAddress == nint.Zero || presetId == Guid.Empty)
            return;

        Call(() => _applyPreset.InvokeAction(presetId, actorAddress), "ApplyPresetByPtrV2");
    }

    /// <inheritdoc/>
    public void RemoveMoodles(nint actorAddress, IReadOnlyList<Guid> moodleIds)
    {
        if (actorAddress == nint.Zero || moodleIds.Count == 0)
            return;

        var list = moodleIds as List<Guid> ?? moodleIds.ToList();
        Call(() => _removeMoodles.InvokeAction(list, actorAddress), "RemoveMoodlesByPtrV2");
    }

    /// <inheritdoc/>
    public void RemovePreset(nint actorAddress, Guid presetId)
    {
        if (actorAddress == nint.Zero || presetId == Guid.Empty)
            return;

        if (_objects.CreateObjectReference(actorAddress) is not IPlayerCharacter player)
        {
            Log.Debug($"[Moodles] RemovePreset: {actorAddress:X} is not a player character; skipped.");
            return;
        }

        Call(() => _removePreset.InvokeAction(presetId, player), "RemovePresetByPlayerV2");
    }

    private void OnStatusManagerModified(nint actorAddress)
        => Dispatch(() => StatusManagerModified?.Invoke(actorAddress), nameof(StatusManagerModified));

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _statusManagerModified.Unsubscribe(OnStatusManagerModified);
        _ready.Unsubscribe(Probe);
        _unloading.Unsubscribe(Probe);
        StatusManagerModified = null;
    }
}
