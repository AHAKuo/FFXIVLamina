using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Luna;
using Newtonsoft.Json.Linq;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using GlamourerInitialized = Glamourer.Api.IpcSubscribers.Initialized;
using GlamourerDisposed = Glamourer.Api.IpcSubscribers.Disposed;

namespace Lamina.Ipc;

/// <summary>
/// Optional capability: release Lamina's lock without reverting the state. Implemented by the real bridge and the test fake;
/// the engine uses it when the "Hold Glamourer state" setting is switched off.
/// </summary>
public interface IGlamourerUnlock
{
    /// <summary> Removes Lamina's lock from the actor's state, leaving the state itself as it is. </summary>
    GlamourerApiEc Unlock(int objectIndex);
}

/// <summary> Glamourer IPC bridge built on the Glamourer.Api subscribers (TDD 11.2). </summary>
public sealed class GlamourerBridge : BridgeBase, IGlamourerBridge, IGlamourerUnlock
{
    /// <summary>
    /// Glamourer answers "Glamourer.ApiVersion.V2" with its own (Major, Minor) pair, which is NOT the label version
    /// (live Glamourer reports 1.x behind the .V2 labels). The labels answering at all is the compatibility proof, so no major gate.
    /// </summary>
    public const int RequiredMajorVersion = -1;

    /// <summary>
    /// The Glamourer.Api submodule declares the StateFinalized label as "Penumbra.StateFinalized" (a historical typo that
    /// Glamourer itself also registers, since it uses the same constant). This is the spelling the UTF8 label uses; we
    /// subscribe to both so the bridge keeps working if Glamourer ever fixes the label.
    /// </summary>
    private const string StateFinalizedAltLabel = "Glamourer.StateFinalized";

    private readonly GlamourerApiVersion     _apiVersion;
    private readonly GetDesignListExtended   _designList;
    private readonly GetDesignJObject        _designJObject;
    private readonly GetState                _getState;
    private readonly ApplyState              _applyState;
    private readonly UnlockState             _unlockState;
    private readonly RevertToAutomation      _revertToAutomation;
    private readonly SetItem                 _setItem;
    private readonly EventSubscriber<nint, StateFinalizationType> _stateFinalized;
    private readonly ICallGateSubscriber<nint, StateFinalizationType, object?>? _stateFinalizedAlt;
    private readonly EventSubscriber         _initialized;
    private readonly EventSubscriber         _disposedEvent;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public GlamourerBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Glamourer", RequiredMajorVersion)
    {
        _apiVersion         = new GlamourerApiVersion(pi);
        _designList         = new GetDesignListExtended(pi);
        _designJObject      = new GetDesignJObject(pi);
        _getState           = new GetState(pi);
        _applyState         = new ApplyState(pi);
        _unlockState        = new UnlockState(pi);
        _revertToAutomation = new RevertToAutomation(pi);
        _setItem            = new SetItem(pi);

        _stateFinalized = Glamourer.Api.IpcSubscribers.StateFinalized.Subscriber(pi, OnStateFinalized);
        try
        {
            _stateFinalizedAlt = pi.GetIpcSubscriber<nint, StateFinalizationType, object?>(StateFinalizedAltLabel);
            _stateFinalizedAlt.Subscribe(OnStateFinalized);
        }
        catch (Exception e)
        {
            _stateFinalizedAlt = null;
            log.Debug($"[Glamourer] could not subscribe to {StateFinalizedAltLabel}: {e.Message}");
        }

        _initialized   = GlamourerInitialized.Subscriber(pi, Probe);
        _disposedEvent = GlamourerDisposed.Subscriber(pi, Probe);

        Probe();
    }

    /// <inheritdoc/>
    public event Action<nint, StateFinalizationType>? StateFinalized;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
        => _apiVersion.Invoke();

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> GetDesigns()
        => Call<IReadOnlyDictionary<Guid, (string, string, uint)>>(() =>
        {
            var raw    = _designList.Invoke();
            var result = new Dictionary<Guid, (string, string, uint)>(raw.Count);
            foreach (var (id, (name, path, color, _)) in raw)
                result[id] = (name, path, color);
            return result;
        }, new Dictionary<Guid, (string, string, uint)>(), nameof(GetDesigns));

    /// <inheritdoc/>
    public JObject? GetDesign(Guid designId)
        => Call(() => _designJObject.Invoke(designId), null, nameof(GetDesign));

    /// <inheritdoc/>
    public (GlamourerApiEc Code, JObject? State) GetState(int objectIndex)
        => Call(() => _getState.Invoke(objectIndex, IpcConstants.GlamourerKey), (GlamourerApiEc.UnknownError, null), nameof(GetState));

    /// <inheritdoc/>
    public GlamourerApiEc ApplyState(JObject state, int objectIndex, bool lockState)
    {
        var flags = ApplyFlag.Equipment | ApplyFlag.Customization;
        if (lockState)
            flags |= ApplyFlag.Lock;
        return Call(() => _applyState.Invoke(state, objectIndex, IpcConstants.GlamourerKey, flags), GlamourerApiEc.UnknownError, nameof(ApplyState));
    }

    /// <inheritdoc/>
    public GlamourerApiEc Unlock(int objectIndex)
        => Call(() => _unlockState.Invoke(objectIndex, IpcConstants.GlamourerKey), GlamourerApiEc.UnknownError, nameof(UnlockState));

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <remarks>
    /// Uses the Lamina key so a state Lamina locked accepts the change, and <see cref="ApplyFlag.Once"/> (the subscriber default):
    /// the item is shown now but not written into the persistent state, so it is a try-on that the next Lamina apply or a revert undoes.
    /// No Lock flag: a try-on must never create a lock Lamina did not ask for.
    /// </remarks>
    /// <summary>
    /// Glamourer reads both stain slots from the list (an empty list throws inside the call); pad with 0 = no dye.
    /// Always a List: Dalamud serializes cross-plugin IPC arguments as JSON and a byte[] becomes a base64 string Glamourer cannot read back.
    /// </summary>
    private static IReadOnlyList<byte> PadStains(IReadOnlyList<byte> stains)
        => new List<byte> { stains.Count > 0 ? stains[0] : (byte)0, stains.Count > 1 ? stains[1] : (byte)0 };

    public GlamourerApiEc SetItem(int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains)
        => Call(() => _setItem.Invoke(objectIndex, slot, itemId, PadStains(stains), IpcConstants.GlamourerKey, ApplyFlag.Once),
            GlamourerApiEc.UnknownError, nameof(SetItem));

    public GlamourerApiEc UnlockAndRevertToAutomation(int objectIndex)
    {
        var unlock = Unlock(objectIndex);
        if (unlock is not GlamourerApiEc.Success and not GlamourerApiEc.NothingDone)
            Log.Debug($"[Glamourer] UnlockState on {objectIndex} returned {unlock}; attempting revert anyway.");

        // After unlocking, the state carries no key, so the revert uses key 0.
        return Call(() => _revertToAutomation.Invoke(objectIndex, 0, ApplyFlagEx.RevertDefault), GlamourerApiEc.UnknownError, nameof(RevertToAutomation));
    }

    private void OnStateFinalized(nint address, StateFinalizationType type)
        => Dispatch(() => StateFinalized?.Invoke(address, type), nameof(StateFinalized));

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _stateFinalized.Dispose();
        _stateFinalizedAlt?.Unsubscribe(OnStateFinalized);
        _initialized.Dispose();
        _disposedEvent.Dispose();
        StateFinalized = null;
    }
}
