using Glamourer.Api.Enums;
using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Models;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Undoes only Lamina's own changes (TDD 7.2 Cleanup / Revert): removes Lamina's temporary Penumbra settings and its decal mod, unlocks and
/// reverts the Glamourer state to automation, clears the Honorific title if Lamina set one, removes the Moodles and presets
/// Lamina applied, then asks for one redraw. Lamina never touches collection assignments: the player manages those in Penumbra.
/// </summary>
public sealed class CleanupService
{
    private readonly IGlamourerBridge _glamourer;
    private readonly IPenumbraBridge _penumbra;
    private readonly IHonorificBridge _honorific;
    private readonly IMoodlesBridge _moodles;
    private readonly IDecalPublisher? _decals;
    private readonly AppliedStateStore _store;
    private readonly RedrawScheduler _redraw;
    private readonly IConditionEvaluator _evaluator;
    private readonly Func<LaminaConfig> _config;
    private readonly Func<Guid?> _collectionId;
    private readonly Func<int?> _localPlayerIndex;
    private readonly Func<nint> _localPlayerAddress;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;

    /// <param name="glamourer"> Glamourer bridge. </param>
    /// <param name="penumbra"> Penumbra bridge. </param>
    /// <param name="honorific"> Honorific bridge. </param>
    /// <param name="moodles"> Moodles bridge. </param>
    /// <param name="decals"> Decal publisher; null when the decal feature could not start. </param>
    /// <param name="store"> Applied state; reset at the end. </param>
    /// <param name="redraw"> Redraw scheduler. </param>
    /// <param name="evaluator"> Condition evaluator; its timers are reset unless the caller keeps them. </param>
    /// <param name="config"> Current config (RedrawPolicy). </param>
    /// <param name="collectionId"> The Lamina collection id, when known. </param>
    /// <param name="localPlayerIndex"> Object index of the local player, or null. </param>
    /// <param name="localPlayerAddress"> Actor address of the local player, 0 when none. </param>
    /// <param name="clock"> UTC clock. </param>
    /// <param name="log"> Optional logger. </param>
    public CleanupService(IGlamourerBridge glamourer, IPenumbraBridge penumbra, IHonorificBridge honorific, IMoodlesBridge moodles,
        IDecalPublisher? decals, AppliedStateStore store, RedrawScheduler redraw, IConditionEvaluator evaluator, Func<LaminaConfig> config,
        Func<Guid?> collectionId, Func<int?> localPlayerIndex, Func<nint> localPlayerAddress, Func<DateTime>? clock = null,
        Action<string>? log = null)
    {
        _glamourer          = glamourer;
        _penumbra           = penumbra;
        _honorific          = honorific;
        _moodles            = moodles;
        _decals             = decals;
        _store              = store;
        _redraw             = redraw;
        _evaluator          = evaluator;
        _config             = config;
        _collectionId       = collectionId;
        _localPlayerIndex   = localPlayerIndex;
        _localPlayerAddress = localPlayerAddress;
        _clock              = clock ?? (() => DateTime.UtcNow);
        _log                = log;
    }

    /// <summary> Outcome of a cleanup, for logging and the UI. </summary>
    public sealed record CleanupResult(string Reason, bool ModsCleared, bool DecalsCleared, bool GlamourerReverted, bool HonorificCleared,
        int MoodlesRemoved, int PresetsRemoved, bool RedrawSent, IReadOnlyList<string> Problems);

    /// <summary> Full cleanup: master toggle off, logout, plugin unload. </summary>
    /// <param name="reason"> Why cleanup runs (logged). </param>
    /// <param name="redrawNow"> Send the redraw immediately instead of leaving it to the scheduler (plugin unload). </param>
    public CleanupResult Cleanup(string reason, bool redrawNow)
        => Cleanup(reason, redrawNow, revertGlamourer: true, resetEvaluator: true);

    /// <summary>
    /// Cleanup before switching to another profile: the collection gets its clean slate, Honorific and Moodles are undone,
    /// but the Glamourer state stays (the next apply replaces or reverts it through the normal diff, so the character never
    /// flashes back to automation for a frame) and the condition timers keep running.
    /// </summary>
    public CleanupResult CleanupForProfileSwitch()
        => Cleanup("profile switch", redrawNow: false, revertGlamourer: false, resetEvaluator: false);

    private CleanupResult Cleanup(string reason, bool redrawNow, bool revertGlamourer, bool resetEvaluator)
    {
        var problems = new List<string>();
        var index    = _localPlayerIndex();
        var config   = _config();

        // 1. Penumbra: remove everything Lamina wrote, in one call.
        var modsCleared = false;
        if (_penumbra.Available && _collectionId() is { } collection)
        {
            var code = _penumbra.RemoveAllTemporaryModSettings(collection);
            modsCleared = code is PenumbraApiEc.Success;
            if (code is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                problems.Add($"RemoveAllTemporaryModSettings: {code}");
        }

        // 1b. Decals: remove Lamina's decal temporary mod (the baked textures stay in the cache folder for reuse).
        var decalsCleared = false;
        if (_store.DecalsApplied && _decals is not null && _penumbra.Available && _collectionId() is { } decalCollection)
        {
            _decals.Clear(decalCollection);
            decalsCleared = true;
        }

        // 2. Glamourer: unlock and hand the character back to automation.
        var reverted = false;
        if (revertGlamourer && _store.GlamourerApplied && _glamourer.Available && index is { } glamIndex)
        {
            var code = _glamourer.UnlockAndRevertToAutomation(glamIndex);
            reverted = code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
            if (!reverted)
                problems.Add($"Glamourer revert: {code}");
        }

        // 3. Honorific: clear only a title Lamina set.
        var honorificCleared = false;
        if (_store.HonorificSet is not null && _honorific.Available && index is { } honIndex)
        {
            honorificCleared = _honorific.SetTitle(honIndex, null);
            if (!honorificCleared)
                problems.Add("Honorific clear failed");
        }

        // 4. Moodles: remove only what Lamina added.
        var moodlesRemoved = 0;
        var presetsRemoved = 0;
        if ((_store.MoodlesAdded.Count > 0 || _store.PresetsApplied.Count > 0) && _moodles.Available)
        {
            var address = _localPlayerAddress();
            if (address != 0)
            {
                if (_store.MoodlesAdded.Count > 0)
                {
                    _moodles.RemoveMoodles(address, _store.MoodlesAdded.ToList());
                    moodlesRemoved = _store.MoodlesAdded.Count;
                }

                foreach (var preset in _store.PresetsApplied)
                {
                    _moodles.RemovePreset(address, preset);
                    presetsRemoved++;
                }

                _store.LastMoodlesCallUtc = _clock();
            }
            else
            {
                problems.Add("Moodles: no actor address");
            }
        }

        // 5. One redraw if the collection changed.
        var redrawSent = false;
        if (modsCleared || _store.ModsApplied || decalsCleared)
        {
            _redraw.Request();
            redrawSent = redrawNow || config.RedrawPolicy == RedrawPolicy.Immediate ? _redraw.RedrawNow() : false;
        }

        _store.Reset(keepGlamourer: !revertGlamourer);
        if (resetEvaluator)
            _evaluator.Reset();

        _log?.Invoke($"Cleanup ({reason}): mods cleared {modsCleared}, decals cleared {decalsCleared}, glamourer reverted {reverted}, honorific cleared {honorificCleared}, "
                   + $"moodles removed {moodlesRemoved}, presets removed {presetsRemoved}, redraw sent {redrawSent}"
                   + (problems.Count > 0 ? $", problems: {string.Join("; ", problems)}" : string.Empty));

        return new CleanupResult(reason, modsCleared, decalsCleared, reverted, honorificCleared, moodlesRemoved, presetsRemoved, redrawSent, problems);
    }
}
