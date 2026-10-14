using Glamourer.Api.Enums;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime.Compose;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Pushes a design's delta to every integration in one batch and in one frame (TDD 7.1 #6, 5.8):
/// Penumbra (clean slate, then the full mod state) → Glamourer (locked state) → Honorific → Moodles → redraw request.
/// Never throws; every outcome lands in the returned <see cref="ApplyInfo"/>. The design hash is recorded only when every
/// attempted section succeeded, so a transient failure (actor not ready, collection missing) is retried on the next evaluation.
/// </summary>
public sealed class Applier
{
    private readonly IGlamourerBridge _glamourer;
    private readonly IPenumbraBridge _penumbra;
    private readonly IHonorificBridge _honorific;
    private readonly IMoodlesBridge _moodles;
    private readonly AppliedStateStore _store;
    private readonly RedrawScheduler _redraw;
    private readonly Func<LaminaConfig> _config;
    private readonly Func<Guid?> _collectionId;
    private readonly Func<CollectionStatus> _collectionStatus;
    private readonly Func<int?> _localPlayerIndex;
    private readonly Func<nint> _localPlayerAddress;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;

    /// <param name="glamourer"> Glamourer bridge. </param>
    /// <param name="penumbra"> Penumbra bridge. </param>
    /// <param name="honorific"> Honorific bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="moodles"> Moodles bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="store"> Applied state, updated after every apply. </param>
    /// <param name="redraw"> Receives the redraw request when mods changed. </param>
    /// <param name="config"> Current config (HoldGlamourerState). </param>
    /// <param name="collectionId"> The Lamina collection id, when found. </param>
    /// <param name="collectionStatus"> Whether the collection is the target's effective collection; mods are skipped otherwise (opt out). </param>
    /// <param name="localPlayerIndex"> Object index of the local player, or null. </param>
    /// <param name="localPlayerAddress"> Actor address of the local player (0 when none), for Moodles. </param>
    /// <param name="clock"> UTC clock. </param>
    /// <param name="log"> Optional debug logger. </param>
    public Applier(IGlamourerBridge glamourer, IPenumbraBridge penumbra, IHonorificBridge honorific, IMoodlesBridge moodles,
        AppliedStateStore store, RedrawScheduler redraw, Func<LaminaConfig> config, Func<Guid?> collectionId,
        Func<CollectionStatus> collectionStatus, Func<int?> localPlayerIndex, Func<nint> localPlayerAddress,
        Func<DateTime>? clock = null, Action<string>? log = null)
    {
        _glamourer          = glamourer;
        _penumbra           = penumbra;
        _honorific          = honorific;
        _moodles            = moodles;
        _store              = store;
        _redraw             = redraw;
        _config             = config;
        _collectionId       = collectionId;
        _collectionStatus   = collectionStatus;
        _localPlayerIndex   = localPlayerIndex;
        _localPlayerAddress = localPlayerAddress;
        _clock              = clock ?? (() => DateTime.UtcNow);
        _log                = log;
    }

    /// <summary> Applies the delta. A <see cref="DesignDelta.NoChange"/> delta only refreshes the store's hash. </summary>
    public ApplyInfo Apply(VirtualDesign design, DesignDelta delta, GameStateSnapshot snapshot)
    {
        var problems = new List<string>();
        var now      = _clock();

        if (delta.NoChange)
        {
            _store.DesignHash = design.ContentHash;
            _store.LastDesign = design;
            return new ApplyInfo(now, design.ContentHash, false, false, 0, false, false, false, false, problems);
        }

        if (_localPlayerIndex() is not { } index)
        {
            problems.Add("no local player");
            _store.DesignHash = null;
            return new ApplyInfo(now, design.ContentHash, false, false, 0, false, false, false, false, problems);
        }

        // True until a section that was attempted failed in a way worth retrying.
        var complete = true;

        // (a) Penumbra: clean slate, then the full mod state.
        var modsApplied  = false;
        var modsWritten  = 0;
        var redrawWanted = false;
        if (delta.ModsChanged)
        {
            var status = _collectionStatus();
            if (status == CollectionStatus.Assigned && _collectionId() is { } collection && _penumbra.Available)
            {
                var hadMods = _store.ModsApplied;
                var clear   = _penumbra.RemoveAllTemporaryModSettings(collection);
                if (clear is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                {
                    problems.Add($"Penumbra clean slate failed: {clear}");
                    complete = false;
                }

                foreach (var (dir, field) in design.Mods)
                {
                    var mod      = field.Value;
                    var settings = ToSettings(mod.Settings);
                    var code     = _penumbra.SetTemporaryModSettings(collection, dir, mod.State == ModToggle.Enable, mod.Priority ?? 0, settings);
                    switch (code)
                    {
                        case PenumbraApiEc.Success:
                            modsWritten++;
                            break;
                        case PenumbraApiEc.ModMissing:
                            // Not transient: the mod is not installed. Reported, not retried.
                            problems.Add($"mod missing: {dir}");
                            break;
                        case PenumbraApiEc.OptionGroupMissing:
                        case PenumbraApiEc.OptionMissing:
                            problems.Add($"option missing in {dir}: {code}");
                            modsWritten++;
                            break;
                        default:
                            problems.Add($"Penumbra refused {dir}: {code}");
                            complete = false;
                            break;
                    }
                }

                modsApplied        = true;
                _store.ModsHash    = complete && design.HasMods ? DesignDiffer.ModsHash(design) : null;
                _store.ModsWritten = modsWritten;
                redrawWanted       = hadMods || modsWritten > 0 || design.HasMods;
            }
            else
            {
                _log?.Invoke($"Mods skipped: collection status {status}.");
                _store.ModsHash    = null;
                _store.ModsWritten = 0;
            }
        }

        // (b) Glamourer: one ApplyState with the merged, masked state.
        var glamourerApplied = false;
        if (delta.GlamourerChanged && _glamourer.Available)
        {
            if (design.HasGlamourerContent)
            {
                // The actor's current state supplies race / clan / gender when the design leaves them unset (see the builder).
                var baseline = _glamourer.GetState(index).State;
                var state    = GlamourerStateBuilder.Build(design, baseline);
                var code  = _glamourer.ApplyState(state, index, _config().HoldGlamourerState);
                if (code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone)
                {
                    glamourerApplied     = true;
                    _store.GlamourerHash = DesignDiffer.GlamourerHash(design);
                }
                else
                {
                    problems.Add($"Glamourer ApplyState failed: {code}");
                    complete = false;
                }
            }
            else if (_store.GlamourerApplied)
            {
                var code = _glamourer.UnlockAndRevertToAutomation(index);
                if (code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone)
                {
                    glamourerApplied     = true;
                    _store.GlamourerHash = null;
                }
                else
                {
                    problems.Add($"Glamourer revert failed: {code}");
                    complete = false;
                }
            }
        }
        else if (delta.GlamourerChanged)
        {
            problems.Add("Glamourer unavailable");
        }

        // (c) Honorific: replace the whole title, or clear it when Lamina had set one.
        var honorificApplied = false;
        if (delta.HonorificChanged && _honorific.Available)
        {
            if (design.Honorific is { } title)
            {
                honorificApplied = _honorific.SetTitle(index, title.Value);
                if (honorificApplied)
                {
                    _store.HonorificSet = title.Value;
                }
                else
                {
                    problems.Add("Honorific SetTitle failed");
                    complete = false;
                }
            }
            else if (_store.HonorificSet is not null)
            {
                honorificApplied = _honorific.SetTitle(index, null);
                if (honorificApplied)
                {
                    _store.HonorificSet = null;
                }
                else
                {
                    problems.Add("Honorific clear failed");
                    complete = false;
                }
            }
        }

        // (d) Moodles: removes first, then adds.
        var moodlesApplied = false;
        if (delta.MoodlesChanged && _moodles.Available)
        {
            var address = _localPlayerAddress();
            if (address == 0)
            {
                problems.Add("Moodles: no actor address");
                complete = false;
            }
            else
            {
                if (delta.MoodleRemoves.Count > 0)
                {
                    _moodles.RemoveMoodles(address, delta.MoodleRemoves);
                    foreach (var id in delta.MoodleRemoves)
                        _store.MoodlesAdded.Remove(id);
                    moodlesApplied = true;
                }

                foreach (var preset in delta.PresetRemoves)
                {
                    _moodles.RemovePreset(address, preset);
                    _store.PresetsApplied.Remove(preset);
                    moodlesApplied = true;
                }

                foreach (var (id, op) in delta.MoodleAdds)
                {
                    if (op == MoodleOp.AddPreset)
                    {
                        _moodles.ApplyPreset(address, id);
                        _store.PresetsApplied.Add(id);
                    }
                    else
                    {
                        _moodles.AddMoodle(address, id);
                        _store.MoodlesAdded.Add(id);
                    }

                    moodlesApplied = true;
                }

                if (moodlesApplied)
                    _store.LastMoodlesCallUtc = now;
            }
        }

        // (e) One redraw, only when mods actually changed in the collection.
        var redrawRequested = false;
        if (redrawWanted)
        {
            _redraw.Request();
            redrawRequested = true;
        }

        _store.DesignHash   = complete ? design.ContentHash : null;
        _store.LastDesign   = design;
        _store.LastApplyUtc = now;

        var deferred = redrawRequested && _redraw.Pending && (snapshot.RedrawUnsafe || _config().RedrawPolicy == RedrawPolicy.Never);
        return new ApplyInfo(now, design.ContentHash, glamourerApplied, modsApplied, modsWritten, honorificApplied, moodlesApplied,
            redrawRequested, deferred, problems);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToSettings(Dictionary<string, List<string>>? settings)
    {
        if (settings is null || settings.Count == 0)
            return new Dictionary<string, IReadOnlyList<string>>();
        return settings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList());
    }
}
