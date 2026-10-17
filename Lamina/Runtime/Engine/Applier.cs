using Glamourer.Api.Enums;
using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime.Compose;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Pushes a design's delta to every integration in one batch and in one frame (TDD 7.1 #6, 5.8):
/// Penumbra (clean slate, then the full mod state) → Glamourer (locked state) → Honorific → Moodles → Customize+ (temporary
/// profile) → Simple Heels (registered offset) → decals (handed to the
/// publisher, which bakes asynchronously and completes with its own redraw request, or parked in the store until the mods
/// redraw has loaded the outfit they go on) → redraw request.
/// Never throws; every outcome lands in the returned <see cref="ApplyInfo"/>. The design hash is recorded only when every
/// attempted section succeeded, so a transient failure (actor not ready, collection missing) is retried on the next evaluation.
/// </summary>
public sealed class Applier
{
    private readonly IGlamourerBridge _glamourer;
    private readonly IPenumbraBridge _penumbra;
    private readonly IHonorificBridge _honorific;
    private readonly IMoodlesBridge _moodles;
    private readonly ICustomizePlusBridge _customizePlus;
    private readonly ISimpleHeelsBridge _heels;
    private readonly IDecalPublisher? _decals;
    private readonly AppliedStateStore _store;
    private readonly RedrawScheduler _redraw;
    private readonly Func<LaminaConfig> _config;
    private readonly Func<Guid?> _collectionId;
    private readonly Func<CollectionStatus> _collectionStatus;
    private readonly bool _writeModsWhenUnassigned;
    private readonly Func<int?> _localPlayerIndex;
    private readonly Func<nint> _localPlayerAddress;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;
    private readonly Func<string?>? _drawObjectKey;

    /// <param name="glamourer"> Glamourer bridge. </param>
    /// <param name="penumbra"> Penumbra bridge. </param>
    /// <param name="honorific"> Honorific bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="moodles"> Moodles bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="customizePlus"> Customize+ bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="heels"> Simple Heels bridge (optional plugin; skipped while unavailable). </param>
    /// <param name="decals"> Decal publisher; null when the decal feature could not start (decal layers are then a reported problem, not a retry). </param>
    /// <param name="store"> Applied state, updated after every apply. </param>
    /// <param name="redraw"> Receives the redraw request when mods changed. </param>
    /// <param name="config"> Current config (HoldGlamourerState). </param>
    /// <param name="collectionId"> The Lamina collection id, when found. </param>
    /// <param name="collectionStatus"> Whether the collection is the target's effective collection; mods are skipped otherwise (opt out). </param>
    /// <param name="localPlayerIndex"> Object index of the local player, or null. </param>
    /// <param name="localPlayerAddress"> Actor address of the local player (0 when none), for Moodles. </param>
    /// <param name="clock"> UTC clock. </param>
    /// <param name="log"> Optional debug logger. </param>
    /// <param name="writeModsWhenUnassigned"> Write mods into the collection even while the character is not assigned to it (other targets: the settings land in the collection either way and show once the character is assigned). The local player opts out by unassigning instead. </param>
    /// <param name="drawObjectKey"> Identity of the player's draw object right now (<see cref="ISkinMeshProvider.DrawObjectState"/>), recorded with an immediate decal publish. </param>
    public Applier(IGlamourerBridge glamourer, IPenumbraBridge penumbra, IHonorificBridge honorific, IMoodlesBridge moodles,
        ICustomizePlusBridge customizePlus, ISimpleHeelsBridge heels,
        IDecalPublisher? decals, AppliedStateStore store, RedrawScheduler redraw, Func<LaminaConfig> config, Func<Guid?> collectionId,
        Func<CollectionStatus> collectionStatus, Func<int?> localPlayerIndex, Func<nint> localPlayerAddress,
        Func<DateTime>? clock = null, Action<string>? log = null, Func<string?>? drawObjectKey = null, bool writeModsWhenUnassigned = false)
    {
        _writeModsWhenUnassigned = writeModsWhenUnassigned;
        _glamourer          = glamourer;
        _penumbra           = penumbra;
        _honorific          = honorific;
        _moodles            = moodles;
        _customizePlus      = customizePlus;
        _heels              = heels;
        _decals             = decals;
        _store              = store;
        _redraw             = redraw;
        _config             = config;
        _collectionId       = collectionId;
        _collectionStatus   = collectionStatus;
        _localPlayerIndex   = localPlayerIndex;
        _localPlayerAddress = localPlayerAddress;
        _clock              = clock ?? (() => DateTime.UtcNow);
        _log                = log;
        _drawObjectKey      = drawObjectKey;
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
            return new ApplyInfo(now, design.ContentHash, false, false, 0, false, false, false, false, false, false, false, false, problems);
        }

        if (_localPlayerIndex() is not { } index)
        {
            problems.Add("no local player");
            _store.DesignHash = null;
            return new ApplyInfo(now, design.ContentHash, false, false, 0, false, false, false, false, false, false, false, false, problems);
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
            if (ModsWritable(status) && _collectionId() is { } collection && _penumbra.Available)
            {
                var hadMods = _store.ModsApplied;

                // Mods this target wrote before and no longer sets go first. Only those: the collection may carry another
                // target's settings (several targets can share one collection), which a clean slate would take with them.
                var stale = _store.ModDirectories.Where(d => !design.Mods.ContainsKey(d)).ToList();
                foreach (var dir in stale)
                {
                    var code = _penumbra.RemoveTemporaryModSettings(collection, dir);
                    if (code is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.ModMissing)
                        _store.ModDirectories.Remove(dir);
                    else
                    {
                        problems.Add($"Penumbra kept {dir}: {code}");
                        complete = false;
                    }
                }

                foreach (var (dir, contribution) in design.Mods)
                {
                    var (enabled, priority, settings) = ResolveModValues(collection, dir, contribution);
                    var code = _penumbra.SetTemporaryModSettings(collection, dir, enabled, priority, settings);
                    switch (code)
                    {
                        case PenumbraApiEc.Success:
                            modsWritten++;
                            _store.ModDirectories.Add(dir);
                            break;
                        case PenumbraApiEc.ModMissing:
                            // Not transient: the mod is not installed. Reported, not retried.
                            problems.Add($"mod missing: {dir}");
                            break;
                        case PenumbraApiEc.OptionGroupMissing:
                        case PenumbraApiEc.OptionMissing:
                            problems.Add($"option missing in {dir}: {code}");
                            modsWritten++;
                            _store.ModDirectories.Add(dir);
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
                _store.ForgetMods();
            }
        }

        // (b) Glamourer: one ApplyState with the merged, masked state. ApplyState leaves fields with Apply=false alone, so a
        // field the previous design set and this one no longer sets would keep Lamina's old value: those are reverted first
        // (to the game state or automation, per config), then the new state is applied in the same frame, which re-asserts
        // the kept fields and re-establishes the lock. Fields only added or changed in value need no revert (no flash).
        var glamourerApplied = false;
        if (delta.GlamourerChanged && _glamourer.Available)
        {
            var fallback = _config().NoRuleFallback;
            if (design.HasGlamourerContent)
            {
                var dropped  = _store.LastGlamourerDesign is { } previous ? DesignDiffer.DroppedGlamourerFields(previous, design) : Array.Empty<string>();
                var reverted = true;
                if (dropped.Count > 0)
                {
                    _log?.Invoke($"Glamourer: no rule sets {string.Join(", ", dropped)} any more; reverting to {fallback} before applying.");
                    var revert = RevertGlamourer(index, fallback);
                    if (revert is not GlamourerApiEc.Success and not GlamourerApiEc.NothingDone)
                    {
                        // The state and LastGlamourerDesign stay as they were, so the next evaluation retries the revert.
                        problems.Add($"Glamourer revert failed: {revert}");
                        complete = reverted = false;
                    }
                }

                if (reverted)
                {
                    // The actor's current state supplies race / clan / gender when the design leaves them unset (see the builder).
                    var baseline = _glamourer.GetState(index).State;
                    var state    = GlamourerStateBuilder.Build(design, baseline);
                    var code     = _glamourer.ApplyState(state, index, _config().HoldGlamourerState);
                    if (code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone)
                    {
                        glamourerApplied           = true;
                        _store.GlamourerHash       = DesignDiffer.GlamourerHash(design);
                        _store.LastGlamourerDesign = design;
                    }
                    else
                    {
                        problems.Add($"Glamourer ApplyState failed: {code}");
                        complete = false;
                        if (dropped.Count > 0)
                        {
                            // The revert went through, so Lamina no longer holds the dropped fields; the retry must not revert again.
                            _store.GlamourerHash       = null;
                            _store.LastGlamourerDesign = null;
                        }
                    }
                }
            }
            else if (_store.GlamourerApplied)
            {
                var code = RevertGlamourer(index, fallback);
                if (code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone)
                {
                    glamourerApplied           = true;
                    _store.GlamourerHash       = null;
                    _store.LastGlamourerDesign = null;
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

        // (d2) Customize+: one temporary profile with the merged bones (Customize+ replaces an earlier temporary profile on the
        // actor itself), or delete Lamina's temporary profile when the design has no bones any more.
        var customizePlusApplied = false;
        if (delta.CustomizePlusChanged && _customizePlus.Available)
        {
            if (design.HasCustomizePlus)
            {
                var bones = design.CustomizePlusBones.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);
                var (code, tempId) = _customizePlus.SetTemporaryProfile(index, bones);
                if (code == CustomizePlusEc.Success)
                {
                    customizePlusApplied            = true;
                    _store.CustomizePlusHash        = DesignDiffer.CustomizePlusHash(design);
                    _store.CustomizePlusTemporaryId = tempId;
                }
                else
                {
                    problems.Add($"Customize+ SetTemporaryProfile failed: {code}");
                    complete = false;
                }
            }
            else if (_store.CustomizePlusApplied)
            {
                var code = _customizePlus.DeleteTemporaryProfile(index);
                // ProfileNotFound: already gone (Customize+ reloaded, or someone else removed it); nothing left to undo.
                if (code is CustomizePlusEc.Success or CustomizePlusEc.ProfileNotFound)
                {
                    customizePlusApplied            = true;
                    _store.CustomizePlusHash        = null;
                    _store.CustomizePlusTemporaryId = null;
                }
                else
                {
                    problems.Add($"Customize+ DeleteTemporaryProfile failed: {code}");
                    complete = false;
                }
            }
        }
        else if (delta.CustomizePlusChanged && design.HasCustomizePlus)
        {
            problems.Add("Customize+ unavailable");
        }

        // (d3) Simple Heels: register one configuration with the resolved height offset (the player's own resolved offset plus the
        // design's for a relative one; the engine re-applies when that base changes), or unregister when the design has none.
        var heelsApplied = false;
        if (delta.HeelsChanged && _heels.Available)
        {
            if (design.SimpleHeels is { } heels)
            {
                var offset     = heels.Value;
                var baseOffset = offset.Relative ? _heels.GetLocalOffset() ?? 0f : 0f;
                if (_heels.RegisterOffset(index, baseOffset + offset.Offset))
                {
                    heelsApplied     = true;
                    _store.HeelsSet  = offset;
                    _store.HeelsBase = offset.Relative ? baseOffset : null;
                }
                else
                {
                    problems.Add("Simple Heels RegisterPlayer failed");
                    complete = false;
                }
            }
            else if (_store.HeelsSet is not null)
            {
                if (_heels.Unregister(index))
                {
                    heelsApplied     = true;
                    _store.HeelsSet  = null;
                    _store.HeelsBase = null;
                }
                else
                {
                    problems.Add("Simple Heels UnregisterPlayer failed");
                    complete = false;
                }
            }
        }
        else if (delta.HeelsChanged && design.SimpleHeels is not null)
        {
            problems.Add("Simple Heels unavailable");
        }

        // (e) Decals: the publisher bakes them into the skin textures and redirects those in the collection, asynchronously; the
        // engine requests the redraw when it completes and forgets the hash again if it fails. Mod changes republish too, since
        // the bake contains the upstream skin texture a body mod may have just changed. When this apply (or an earlier one)
        // asked for a redraw, the outfit on screen is not the one the decals go on: the publisher would snapshot the old skin
        // meshes and resolve the old texture. The decals then wait in the store and DecalFollowUp publishes them once the
        // redrawn character has loaded. Under RedrawPolicy.Never that redraw never goes out by itself, so the decals stay
        // pending until a manual redraw or a draw object change (login, zone change, gear swap) hands them to the follow-up.
        var decalsPublished = false;
        var decalsDeferred  = false;
        if (delta.DecalsChanged || (delta.ModsChanged && design.HasDecals))
        {
            if (_decals is null)
            {
                // Not transient: the feature could not start. Reported, not retried.
                if (design.HasDecals)
                    problems.Add("decals unavailable");
                _store.ForgetDecals();
            }
            else
            {
                var status = _collectionStatus();
                if (status == CollectionStatus.Assigned && _collectionId() is { } collection && _penumbra.Available)
                {
                    if (design.HasDecals)
                    {
                        var specs = design.Decals.Select(f => f.Value).ToList();
                        _store.DecalsHash = DesignDiffer.DecalsHash(design);
                        if (redrawWanted || _redraw.Pending)
                        {
                            _store.PendingDecals = specs;
                            decalsDeferred       = true;
                            _log?.Invoke("Decals deferred until the redraw has loaded the new outfit.");
                        }
                        else
                        {
                            _store.PendingDecals = null;
                            _decals.Publish(collection, index, specs, _store.ModsHash);
                            _store.DecalsDrawKey = _drawObjectKey?.Invoke();
                            decalsPublished      = true;
                        }
                    }
                    else if (_store.DecalsApplied)
                    {
                        _decals.Clear(collection);
                        _store.ForgetDecals();
                        redrawWanted = true;
                    }
                }
                else
                {
                    _log?.Invoke($"Decals skipped: collection status {status}.");
                    _store.ForgetDecals();
                }
            }
        }

        // (f) One redraw, only when mods actually changed in the collection.
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
            customizePlusApplied, heelsApplied, decalsPublished, decalsDeferred, redrawRequested, deferred, problems);
    }

    /// <summary> Unlocks and reverts the actor's Glamourer state to the configured fallback (TDD: fall back to the game state when no rule applies). </summary>
    private GlamourerApiEc RevertGlamourer(int index, NoRuleFallback fallback)
        => fallback == NoRuleFallback.Automation
            ? _glamourer.UnlockAndRevertToAutomation(index)
            : _glamourer.UnlockAndRevertToGame(index);

    /// <summary>
    /// Completes a contribution with the parts it inherits (TDD 5.7): the mod's permanent settings in the collection
    /// (temporary ones ignored, so another plugin's preview is never baked in). A mod not configured there inherits
    /// "disabled, priority 0, Penumbra's default options" — which is what omitting the groups gives.
    /// </summary>
    private bool ModsWritable(CollectionStatus status)
        => status == CollectionStatus.Assigned || (status == CollectionStatus.NotAssigned && _writeModsWhenUnassigned);

    private (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings) ResolveModValues(
        Guid collection, string dir, ModContribution c)
    {
        // Option groups the entry does not list are always inherited, so the lookup is needed for every mod (one IPC call
        // per mod per apply; applies only happen when the design changed).
        var current = _penumbra.GetCurrentModSettings(collection, dir, ignoreTemporary: true);

        var enabled  = c.State is { } state ? state.Value == ModToggle.Enable : current?.Enabled ?? false;
        var priority = c.Priority is { } prio ? prio.Value : current?.Priority ?? 0;

        var settings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (current is { } cur)
            foreach (var (group, options) in cur.Settings)
                settings[group] = options.ToList();
        foreach (var (group, field) in c.Settings)
            settings[group] = field.Value.ToList();

        return (enabled, priority, settings);
    }
}
