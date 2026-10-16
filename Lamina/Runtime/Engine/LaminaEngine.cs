using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Persistence;
using Lamina.Runtime.Compose;
using Lamina.Runtime.State;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Drives the pipeline from the framework thread (TDD 7.1): listens to the state tracker, profile store, Vitrine and the
/// bridges, re-evaluates when something relevant changed or an evaluator timer elapsed, applies once per real change and
/// schedules at most one redraw. Owns collection detection (read only: the player assigns the collection in Penumbra) and
/// the master toggle.
/// </summary>
public sealed class LaminaEngine : ILaminaEngine, IDisposable
{
    private static readonly TimeSpan ReassertThrottle    = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowTickInterval    = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NamesRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MoodlesOwnCallWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MoodlesEventThrottle = TimeSpan.FromSeconds(1);

    private readonly ProfileStore _profiles;
    private readonly StateTracker _tracker;
    private readonly ConditionEvaluator _evaluator;
    private readonly PassCompositor _compositor;
    private readonly LayerResolver _resolver;
    private readonly GlamourerDesignCache _cache;
    private readonly IGlamourerBridge _glamourer;
    private readonly IPenumbraBridge _penumbra;
    private readonly IHonorificBridge _honorific;
    private readonly IMoodlesBridge _moodles;
    private readonly ICustomizePlusBridge _customizePlus;
    private readonly ISimpleHeelsBridge _heels;
    private readonly CustomizePlusProfileCache _customizePlusCache;
    private readonly IVitrineBridge _vitrine;
    private readonly IDecalPublisher? _decals;
    private readonly ISkinMeshProvider? _meshes;
    private readonly DecalFollowUp? _followUp;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly GameCatalog _catalog;
    private readonly IPluginLog _log;
    private readonly TickClock _clock;

    private readonly AppliedStateStore _store = new();
    private readonly RedrawScheduler _redraw;
    private readonly Applier _applier;
    private readonly CleanupService _cleanup;
    private readonly EngineCore _core;

    private bool _dirty = true;
    private bool _force;

    /// <summary> After an apply with problems, wait this long before trying again so a persistent failure does not hammer the IPC every tick. </summary>
    private static readonly TimeSpan ApplyRetryBackoff = TimeSpan.FromSeconds(3);
    private DateTime? _retryDue;
    private bool _applying;
    private bool _reassertGlamourer;
    private bool _lastHoldState;
    private DateTime _lastReassert     = DateTime.MinValue;
    private DateTime _lastSlowTick     = DateTime.MinValue;
    private DateTime _lastNamesRefresh = DateTime.MinValue;
    private DateTime _lastMoodlesEvent = DateTime.MinValue;
    private DateTime? _timerDue;
    private CollectionStatus _collectionStatus = CollectionStatus.PenumbraMissing;
    private Guid? _lastAssignedCollection;
    private bool _disposed;

    /// <summary>
    /// Creates the engine and subscribes to every source of change. <paramref name="decalPublisher"/> is null when the decal
    /// feature could not start; decal layers are then reported as a problem instead of applied. <paramref name="skinMeshes"/>
    /// supplies the draw object identity the decal follow-up watches.
    /// </summary>
    public LaminaEngine(ProfileStore profiles, StateTracker tracker, ConditionEvaluator evaluator, PassCompositor compositor,
        LayerResolver resolver, GlamourerDesignCache cache, IGlamourerBridge glamourer, IPenumbraBridge penumbra,
        IHonorificBridge honorific, IMoodlesBridge moodles, ICustomizePlusBridge customizePlus, ISimpleHeelsBridge heels,
        CustomizePlusProfileCache customizePlusCache, IVitrineBridge vitrine, IFramework framework, IObjectTable objects,
        IClientState clientState, ICondition condition, GameCatalog catalog, IPluginLog log, TickClock? clock = null,
        IDecalPublisher? decalPublisher = null, ISkinMeshProvider? skinMeshes = null)
    {
        _profiles    = profiles;
        _tracker     = tracker;
        _evaluator   = evaluator;
        _compositor  = compositor;
        _resolver    = resolver;
        _cache       = cache;
        _glamourer   = glamourer;
        _penumbra    = penumbra;
        _honorific   = honorific;
        _moodles     = moodles;
        _customizePlus      = customizePlus;
        _heels              = heels;
        _customizePlusCache = customizePlusCache;
        _vitrine     = vitrine;
        _decals      = decalPublisher;
        _meshes      = skinMeshes;
        _framework   = framework;
        _objects     = objects;
        _clientState = clientState;
        _condition   = condition;
        _catalog     = catalog;
        _log         = log;
        _clock       = clock ?? new TickClock();

        _redraw  = new RedrawScheduler(_penumbra, LocalPlayerIndex, m => _log.Debug(m));
        _applier = new Applier(_glamourer, _penumbra, _honorific, _moodles, _customizePlus, _heels, _decals, _store, _redraw, () => _profiles.Config, () => CollectionId,
            () => _collectionStatus, LocalPlayerIndex, LocalPlayerAddress, _clock.Read, m => _log.Debug(m), DrawObjectKey);
        _cleanup = new CleanupService(_glamourer, _penumbra, _honorific, _moodles, _customizePlus, _heels, _decals, _store, _redraw, _evaluator, () => _profiles.Config,
            () => CollectionId, LocalPlayerIndex, LocalPlayerAddress, _clock.Read, m => _log.Information(m));
        _core = new EngineCore(_compositor, _resolver, _evaluator, _applier, _cleanup, _store, () => _profiles.Config);
        if (_decals is not null)
            _followUp = new DecalFollowUp(_store, _clock.Read, DrawObject, DecalsReady, () => _decals.Busy, PublishDecals, m => _log.Debug(m));

        _lastHoldState = _profiles.Config.HoldGlamourerState;

        _tracker.SnapshotChanged       += OnSnapshotChanged;
        _profiles.Changed              += OnProfilesChanged;
        _vitrine.DesignsChanged        += OnVitrineChanged;
        _glamourer.AvailabilityChanged += OnGlamourerAvailability;
        _penumbra.AvailabilityChanged  += OnPenumbraAvailability;
        _honorific.AvailabilityChanged += Invalidate;
        _moodles.AvailabilityChanged   += Invalidate;
        _customizePlus.AvailabilityChanged += OnCustomizePlusAvailability;
        _customizePlus.ProfilesChanged     += OnCustomizePlusProfilesChanged;
        _heels.AvailabilityChanged         += OnHeelsAvailability;
        _heels.LocalOffsetChanged          += OnHeelsLocalOffsetChanged;
        _glamourer.StateFinalized      += OnStateFinalized;
        if (_moodles is IMoodlesStatusEvents moodleEvents)
            moodleEvents.StatusManagerModified += OnMoodlesStatusManagerModified;
        _clientState.Login             += OnLogin;
        _clientState.Logout            += OnLogout;
        _condition.ConditionChange     += OnConditionChange;
        _framework.Update              += OnFrameworkUpdate;
        if (_decals is not null)
            _decals.Completed += OnDecalPublishCompleted;
        _penumbra.GameObjectRedrawn    += OnGameObjectRedrawn;

        // Dalamud constructs plugins off the main thread and the object table refuses off-thread access,
        // so the first collection check runs on the first framework tick instead of here.
        _collectionRefreshPending = true;
    }

    private bool _collectionRefreshPending;

    // ---- ILaminaEngine ----------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Enabled
        => _profiles.Config.Enabled;

    /// <inheritdoc/>
    public GameStateSnapshot Snapshot
        => _tracker.Current;

    /// <inheritdoc/>
    public VirtualDesign? LastDesign { get; private set; }

    /// <inheritdoc/>
    public EvaluationReport? LastReport { get; private set; }

    /// <inheritdoc/>
    public ApplyInfo? LastApply { get; private set; }

    /// <inheritdoc/>
    public bool RedrawPending
        => _redraw.Pending;

    /// <inheritdoc/>
    public CollectionStatus CollectionStatus
        => _collectionStatus;

    /// <inheritdoc/>
    public Guid? CollectionId { get; private set; }

    /// <inheritdoc/>
    public event Action? Applied;

    /// <summary> The applied-state store, for the debug overlay. </summary>
    public AppliedStateStore AppliedState
        => _store;

    /// <inheritdoc/>
    public void SetEnabled(bool enabled)
    {
        if (_profiles.Config.Enabled == enabled)
            return;

        _profiles.Config.Enabled = enabled;
        _profiles.MarkConfigDirty();
        if (enabled)
        {
            RefreshCollection();
            RequestReapply();
        }
        else
        {
            RunCleanup("master toggle off", redrawNow: false);
            Applied?.Invoke();
        }
    }

    /// <inheritdoc/>
    public void RefreshCollection()
    {
        var previous = _collectionStatus;
        if (!_penumbra.Available)
        {
            CollectionId      = null;
            _collectionStatus = CollectionStatus.PenumbraMissing;
        }
        else
        {
            CollectionId = _penumbra.FindCollection(_profiles.Config.CollectionName);
            if (CollectionId is null)
                _collectionStatus = CollectionStatus.NotFound;
            else if (LocalPlayerIndex() is { } index)
                _collectionStatus = ReadAssignment(index);
            else
                _collectionStatus = previous is CollectionStatus.PenumbraMissing or CollectionStatus.NotFound ? CollectionStatus.NotAssigned : previous;
        }

        OnCollectionStatusChanged(previous);
    }

    /// <inheritdoc/>
    public void RequestReapply()
    {
        _cache.InvalidateAll();
        _customizePlusCache.InvalidateAll();
        _vitrine.RefreshDesigns();
        _dirty = true;
        _force = true;
    }

    /// <inheritdoc/>
    public void Invalidate()
        => _dirty = true;

    /// <summary> Forgets what Lamina wrote into the collection (someone else wrote there with Lamina's key) so the next apply rewrites it. </summary>
    public void InvalidateMods()
    {
        _store.ModsHash   = null;
        _store.DesignHash = null;
        _dirty            = true;
    }

    /// <inheritdoc/>
    public bool RedrawNow()
    {
        _redraw.Request();
        return _redraw.RedrawNow();
    }

    /// <inheritdoc/>
    public bool DecalsApplied
        => _store.DecalsApplied;

    /// <inheritdoc/>
    public void ForgetDecals(bool redraw)
    {
        _store.ForgetDecals();
        _store.DesignHash = null;
        _followUp?.Reset();
        if (redraw)
            _redraw.Request();
        _dirty = true;
    }

    /// <inheritdoc/>
    public PreviewResult Preview(Profile profile, Guid? focusGroupId, Guid? focusPassId)
        => _compositor.Preview(profile, _tracker.Current, _evaluator, _profiles.Config.Direction, _resolver, focusGroupId, focusPassId);

    /// <inheritdoc/>
    public string Why()
    {
        var s  = _tracker.Current;
        var sb = new StringBuilder();
        var profile = _profiles.ActiveProfileFor(Target.LocalPlayer());
        sb.Append("Lamina: ").Append(Enabled ? "enabled" : "disabled").Append(" · profile: ").AppendLine(profile?.Name ?? "(none)");
        sb.Append("Collection: ").Append(_profiles.Config.CollectionName).Append(" · ").AppendLine(_collectionStatus switch
        {
            CollectionStatus.PenumbraMissing => "Penumbra missing",
            CollectionStatus.NotFound        => "not found in Penumbra (create it there)",
            CollectionStatus.NotAssigned     => "not assigned to your character in Penumbra (mods skipped)",
            _                                => "assigned to your character",
        });
        sb.Append("Integrations: ")
          .Append(Flag("Glamourer", _glamourer.Available)).Append(", ")
          .Append(Flag("Penumbra", _penumbra.Available)).Append(", ")
          .Append(Flag("Honorific", _honorific.Available)).Append(", ")
          .Append(Flag("Moodles", _moodles.Available)).Append(", ")
          .Append(Flag("Customize+", _customizePlus.Available)).Append(", ")
          .Append(Flag("Simple Heels", _heels.Available)).Append(", ")
          .AppendLine(_decals is null ? "Decals (unavailable)" : "Decals");
        sb.Append("State: ").Append(_catalog.TerritoryName(s.TerritoryId)).Append(" (#").Append(s.TerritoryId).Append(") · ")
          .Append(_catalog.WeatherName(s.WeatherId)).Append(" · ET ").Append(s.EorzeaTime.ToString(@"hh\:mm")).Append(" · ")
          .Append(_catalog.JobName(s.ClassJobId)).Append(" L").Append(s.Level).Append(" · ")
          .Append(_catalog.OnlineStatusName(s.OnlineStatusId)).AppendLine();
        var flags = s.Movement.Select(m => m.ToString()).Concat(s.Body.Select(b => b.ToString())).Concat(s.Activities.Select(a => a.ToString())).ToList();
        if (s.Pose is { } pose) flags.Add(pose.ToString());
        if (s.InDuty) flags.Add("InDuty");
        if (s.IsSanctuary) flags.Add("Sanctuary");
        if (s.IsIndoors) flags.Add("Indoors");
        sb.Append("Flags: ").AppendLine(flags.Count == 0 ? "(none)" : string.Join(", ", flags));
        if (LastApply is { } apply)
        {
            sb.Append("Last apply: ").Append(apply.AtUtc.ToLocalTime().ToString("HH:mm:ss")).Append(" · glamourer ").Append(apply.GlamourerApplied)
              .Append(" · mods ").Append(apply.ModsWritten).Append(" · decals ").Append(apply.DecalsPublished ? "published" : apply.DecalsDeferred ? "pending redraw" : "none")
              .Append(" · redraw ").Append(apply.RedrawRequested ? (apply.RedrawDeferred ? "deferred" : "yes") : "no");
            if (apply.Problems.Count > 0)
                sb.Append(" · problems: ").Append(string.Join("; ", apply.Problems));
            sb.AppendLine();
        }

        if (_redraw.Pending)
            sb.AppendLine("Redraw pending.");

        if (LastDesign is { } design && profile is not null)
        {
            var groups = profile.PassGroups.ToDictionary(g => g.Id, g => g.Name);
            var passes = profile.PassGroups.SelectMany(g => g.Passes).ToDictionary(p => p.Id, p => p.Name);
            sb.AppendLine(DesignExplainer.Explain(design,
                id => groups.TryGetValue(id, out var n) ? n : id.ToString(),
                id => passes.TryGetValue(id, out var n) ? n : id.ToString(),
                LastReport?.SkippedLayers));
        }
        else
        {
            sb.AppendLine("No design evaluated yet.");
        }

        return sb.ToString().TrimEnd();
    }

    // ---- loop -------------------------------------------------------------------------------------

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed)
            return;

        var now = _clock.Advance();
        try
        {
            _profiles.Tick(now);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Saving failed.");
        }

        if (_collectionRefreshPending)
        {
            _collectionRefreshPending = false;
            try
            {
                RefreshCollection();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Initial collection check failed.");
            }
        }

        try
        {
            if (_timerDue is { } due && now >= due)
            {
                _timerDue = null;
                _dirty    = true;
            }

            if (_reassertGlamourer && now - _lastReassert >= ReassertThrottle)
            {
                // Glamourer automation wrote over Lamina's state: forget only the Glamourer section so the normal diff
                // re-applies it. Never a forced full apply (that would clear and rewrite every mod and redraw).
                _reassertGlamourer   = false;
                _lastReassert        = now;
                _store.GlamourerHash = null;
                _store.DesignHash    = null;
                _dirty               = true;
            }

            CheckHoldStateFlip();

            if (now - _lastSlowTick >= SlowTickInterval)
            {
                _lastSlowTick = now;
                SlowTick(now);
            }

            if (_dirty)
                Evaluate(now);

            if (_redraw.Tick(_tracker.Current, _profiles.Config.RedrawPolicy))
                _log.Information("Redraw sent to Penumbra.");
            else if (_redraw.Pending && _tracker.Current.RedrawUnsafe)
                _log.Debug("Redraw deferred: unsafe right now.");

            // Decals wait for the redrawn character to load, then publish against what is drawn.
            _followUp?.Tick();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Engine update failed.");
            _dirty = false;
            _force = false;
        }
    }

    private void SlowTick(DateTime now)
    {
        // Opt-in / opt-out detection: the player may (un)assign the collection in Penumbra at any time.
        if (_penumbra.Available)
        {
            if (_collectionStatus is CollectionStatus.PenumbraMissing or CollectionStatus.NotFound)
            {
                RefreshCollection();
            }
            else if (CollectionId is not null && LocalPlayerIndex() is { } index)
            {
                var previous = _collectionStatus;
                _collectionStatus = ReadAssignment(index);
                OnCollectionStatusChanged(previous);
            }
        }

        if (_glamourer.Available && now - _lastNamesRefresh >= NamesRefreshInterval)
        {
            _lastNamesRefresh = now;
            if (_cache.RefreshNames())
                _dirty = true;
        }

        // Draw object changes the game makes on its own (login, zone change, gear swap) raise no Penumbra event.
        _followUp?.Poll();
    }

    private void CheckHoldStateFlip()
    {
        var hold = _profiles.Config.HoldGlamourerState;
        if (hold == _lastHoldState)
            return;

        _lastHoldState = hold;
        if (!_store.GlamourerApplied)
            return;

        if (!hold && _glamourer is IGlamourerUnlock unlock && LocalPlayerIndex() is { } index)
        {
            var code = unlock.Unlock(index);
            _log.Debug($"Hold Glamourer state switched off: unlock returned {code}.");
        }

        // Re-apply so the state carries (or drops) the lock.
        _store.GlamourerHash = null;
        _store.DesignHash    = null;
        _dirty               = true;
    }

    private void Evaluate(DateTime now)
    {
        if (!Enabled)
        {
            _dirty = false;
            _force = false;
            return;
        }

        // Keep the request alive until there is a player to apply to (loading screens, login).
        if (!_clientState.IsLoggedIn || LocalPlayerIndex() is null)
            return;

        // Back off after a failed apply (keeps _dirty so the retry happens when due); a forced reapply skips the wait.
        if (!_force && _retryDue is { } retryDue && now < retryDue)
            return;

        var snapshot = _tracker.Current;
        var profile  = _profiles.ActiveProfileFor(Target.LocalPlayer());
        var force    = _force;
        _dirty = false;
        _force = false;

        EngineStep step;
        _applying = true;
        try
        {
            step = _core.Step(snapshot, profile, force);
        }
        finally
        {
            _applying = false;
        }

        LastDesign = step.Design;
        LastReport = step.Report;
        if (step.Apply is { } apply)
        {
            LastApply = apply;
            if (apply.Problems.Count > 0)
            {
                _log.Warning($"Apply problems: {string.Join("; ", apply.Problems)}");
                _retryDue = now + ApplyRetryBackoff;
            }
            else
            {
                _log.Debug($"Applied {apply.DesignHash}: glamourer {apply.GlamourerApplied}, mods {apply.ModsWritten}, redraw {apply.RedrawRequested}");
                _retryDue = null;
            }
            Applied?.Invoke();
        }
        else if (step.ProfileSwitched)
        {
            Applied?.Invoke();
        }

        _timerDue = _evaluator.TimeUntilNextChange is { } wait ? now + wait : null;
    }

    // ---- collection -------------------------------------------------------------------------------

    private CollectionStatus ReadAssignment(int index)
    {
        var (valid, _, effective, _) = _penumbra.GetCollectionForObject(index);
        return valid && CollectionId is { } collection && effective == collection ? CollectionStatus.Assigned : CollectionStatus.NotAssigned;
    }

    private void OnCollectionStatusChanged(CollectionStatus previous)
    {
        if (_collectionStatus == CollectionStatus.Assigned)
            _lastAssignedCollection = CollectionId;

        if (previous == _collectionStatus)
            return;

        _log.Debug($"Collection status: {previous} → {_collectionStatus}");

        if (previous == CollectionStatus.Assigned)
        {
            // Opt out: the player removed the assignment (or the collection vanished). Leave nothing of Lamina's behind.
            if (_penumbra.Available && _lastAssignedCollection is { } old)
            {
                var code = _penumbra.RemoveAllTemporaryModSettings(old);
                if (code is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                    _log.Warning($"Could not clear Lamina's settings from the previous collection: {code}");
                if (_store.DecalsApplied)
                    _decals?.Clear(old);
            }

            _store.ModsHash    = null;
            _store.ModsWritten = 0;
            _store.ForgetDecals();
            _followUp?.Reset();
            _log.Information("The Lamina collection is no longer assigned to your character; mods are skipped until it is assigned again in Penumbra.");
        }
        else if (_collectionStatus == CollectionStatus.Assigned)
        {
            // Opt in: write the full mod state on the next evaluation.
            _store.ModsHash   = null;
            _store.DesignHash = null;
        }

        _dirty = true;
    }

    // ---- events -----------------------------------------------------------------------------------

    private void OnSnapshotChanged(GameStateSnapshot snapshot)
        => _dirty = true;

    private void OnProfilesChanged()
        => _dirty = true;

    private void OnVitrineChanged()
        => _dirty = true;

    private void OnGlamourerAvailability()
    {
        _cache.InvalidateAll();
        if (_glamourer.Available)
        {
            RequestReapply();
        }
        else
        {
            _store.GlamourerHash = null;
            _store.DesignHash    = null;
        }
    }

    private void OnCustomizePlusAvailability()
    {
        _customizePlusCache.InvalidateAll();
        if (!_customizePlus.Available)
        {
            // The temporary profile died with the plugin; nothing to undo, but the next apply must set it again.
            _store.CustomizePlusHash        = null;
            _store.CustomizePlusTemporaryId = null;
        }

        // Either way the design hash no longer tells the whole story (the section was skipped or lost): diff again.
        _store.DesignHash = null;
        _dirty            = true;
    }

    /// <summary> Customize+ reported a profile change: its bones may differ, so the cached profiles are dropped and the design re-evaluated. </summary>
    private void OnCustomizePlusProfilesChanged()
    {
        _customizePlusCache.InvalidateAll();
        _dirty = true;
    }

    private void OnHeelsAvailability()
    {
        if (!_heels.Available)
        {
            // Simple Heels forgets IPC registrations when it unloads; re-register on the next apply.
            _store.HeelsSet  = null;
            _store.HeelsBase = null;
        }

        _store.DesignHash = null;
        _dirty            = true;
    }

    /// <summary>
    /// The player's own Simple Heels offset changed (footwear swap, edited configuration). A relative registration was built on the
    /// old value, so it is forgotten and the next evaluation registers again on the new base. Absolute registrations are unaffected.
    /// </summary>
    private void OnHeelsLocalOffsetChanged(float offset)
    {
        if (_store.HeelsSet is not { Relative: true } || _store.HeelsBase is not { } baseOffset || MathF.Abs(baseOffset - offset) < 0.0001f)
            return;

        _log.Debug($"Simple Heels: player offset changed {baseOffset:0.###} → {offset:0.###}; re-registering the relative offset.");
        _store.HeelsSet   = null;
        _store.HeelsBase  = null;
        _store.DesignHash = null;
        _dirty            = true;
    }

    private void OnPenumbraAvailability()
    {
        if (!_penumbra.Available)
        {
            _store.ModsHash    = null;
            _store.ModsWritten = 0;
            _store.ForgetDecals();
            _followUp?.Reset();
            _redraw.Cancel();
        }

        RefreshCollection();
        RequestReapply();
    }

    private void OnStateFinalized(nint address, StateFinalizationType type)
    {
        // Gearset is deliberately not a trigger: Glamourer reports it on every gearset change (and on initial load), which
        // must not cost a re-apply. Whether a locked state survives gearset changes is verified in game (TDD 11.2).
        if (type is not (StateFinalizationType.ReapplyAutomation or StateFinalizationType.RevertAutomation))
            return;
        if (_applying || !_store.GlamourerApplied || address == 0 || address != LocalPlayerAddress())
            return;

        _log.Debug($"Glamourer finalized {type} on the local player; re-asserting Lamina's state.");
        _reassertGlamourer = true;
    }

    private void OnMoodlesStatusManagerModified(nint address)
    {
        if (address == 0 || address != LocalPlayerAddress())
            return;
        if (_store.MoodlesAdded.Count == 0 && _store.PresetsApplied.Count == 0)
            return;

        var now = _clock.Now;
        if (_store.LastMoodlesCallUtc is { } own && now - own < MoodlesOwnCallWindow)
            return;
        if (now - _lastMoodlesEvent < MoodlesEventThrottle)
            return;

        // Something else changed the status manager (a moodle expired or was removed): forget what Lamina added so the
        // next evaluation re-adds whatever the design still wants.
        _lastMoodlesEvent = now;
        _store.MoodlesAdded.Clear();
        _store.PresetsApplied.Clear();
        _store.DesignHash = null;
        _dirty            = true;
    }

    private void OnDecalPublishCompleted(DecalPublishResult result)
    {
        if (_disposed)
            return;

        if (!_framework.IsInFrameworkUpdateThread)
        {
            // The publisher completes on the framework thread; this only guards a foreign implementation.
            _ = _framework.RunOnFrameworkThread(() => OnDecalPublishCompleted(result));
            return;
        }

        foreach (var problem in result.Problems)
            _log.Warning($"Decal publish: {problem}");

        // The character picks the baked textures up on redraw, but only when the redirects differ from what it already
        // loaded (an identical re-install after a mods redraw needs none, which spares the second load).
        var redraw = _followUp?.OnPublishCompleted(result) ?? DecalFollowUp.RedrawWanted(result, false);
        if (redraw)
        {
            _redraw.Request();
            _log.Information($"Decal publish completed: {result.Published} surface(s) redirected; redraw requested "
                           + $"(policy {_profiles.Config.RedrawPolicy}, unsafe now: {_tracker.Current.RedrawUnsafe}).");
        }
        else if (result.Published > 0)
        {
            _log.Information($"Decal publish completed: {result.Published} surface(s) redirected, unchanged; no redraw needed.");
        }

        if (result.Success)
        {
            _log.Debug($"Decals published: {result.Published} surface(s) redirected.");
            return;
        }

        // Forget the decal section so the next evaluation, whenever something else triggers it, publishes again. Nothing is
        // triggered here: a persistent failure must not loop, and the apply backoff spaces the retries out.
        _store.ForgetDecals();
        _store.DesignHash = null;
        _retryDue         = _clock.Now + ApplyRetryBackoff;
    }

    private void OnGameObjectRedrawn(nint address, int objectIndex)
    {
        if (_disposed || _followUp is null)
            return;

        // Penumbra raises this on the framework thread for every object it redraws (ours or another plugin's).
        if (LocalPlayerIndex() is { } index && objectIndex == index)
            _followUp.Notice("the player was redrawn");
    }

    private void OnLogin()
    {
        RefreshCollection();
        RequestReapply();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        // The actor still exists here, so Glamourer / Honorific / Moodles can be undone; on Logout it is already gone.
        if (flag == ConditionFlag.LoggingOut && value && _store.AnythingApplied)
            RunCleanup("logging out", redrawNow: false);
    }

    private void OnLogout(int type, int code)
    {
        if (_store.AnythingApplied)
            RunCleanup("logout", redrawNow: false);

        _core.ForgetProfile();
        _redraw.Cancel();
        _followUp?.Reset();
        LastDesign = null;
        LastReport = null;
    }

    private void RunCleanup(string reason, bool redrawNow)
    {
        _applying = true;
        try
        {
            _cleanup.Cleanup(reason, redrawNow);
        }
        finally
        {
            _applying = false;
        }

        _core.ForgetProfile();
        _followUp?.Reset();
        LastDesign = null;
        LastReport = null;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary> The object table only answers on the framework thread; off-thread callers see "no player". </summary>
    private int? LocalPlayerIndex()
        => _framework.IsInFrameworkUpdateThread && _objects.LocalPlayer is { } player ? player.ObjectIndex : null;

    private nint LocalPlayerAddress()
        => _framework.IsInFrameworkUpdateThread ? _objects.LocalPlayer?.Address ?? 0 : 0;

    private DrawObjectIdentity? DrawObject()
        => _framework.IsInFrameworkUpdateThread ? _meshes?.DrawObjectState() : null;

    private string? DrawObjectKey()
        => DrawObject()?.Key;

    /// <summary> A decal publish can go out: Lamina on, Penumbra there, the collection assigned, a player to draw. </summary>
    private bool DecalsReady()
        => Enabled && _penumbra.Available && _collectionStatus == CollectionStatus.Assigned && CollectionId is not null && LocalPlayerIndex() is not null;

    private void PublishDecals(IReadOnlyList<DecalSpec> decals)
    {
        if (_decals is not null && CollectionId is { } collection && LocalPlayerIndex() is { } index)
            _decals.Publish(collection, index, decals, _store.ModsHash);
    }

    private static string Flag(string name, bool on)
        => on ? name : $"{name} (missing)";

    /// <summary> Runs the unload cleanup and unsubscribes. Safe to call twice. </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _framework.Update              -= OnFrameworkUpdate;
        _tracker.SnapshotChanged       -= OnSnapshotChanged;
        _profiles.Changed              -= OnProfilesChanged;
        _vitrine.DesignsChanged        -= OnVitrineChanged;
        _glamourer.AvailabilityChanged -= OnGlamourerAvailability;
        _penumbra.AvailabilityChanged  -= OnPenumbraAvailability;
        _honorific.AvailabilityChanged -= Invalidate;
        _moodles.AvailabilityChanged   -= Invalidate;
        _customizePlus.AvailabilityChanged -= OnCustomizePlusAvailability;
        _customizePlus.ProfilesChanged     -= OnCustomizePlusProfilesChanged;
        _heels.AvailabilityChanged         -= OnHeelsAvailability;
        _heels.LocalOffsetChanged          -= OnHeelsLocalOffsetChanged;
        _glamourer.StateFinalized      -= OnStateFinalized;
        if (_moodles is IMoodlesStatusEvents moodleEvents)
            moodleEvents.StatusManagerModified -= OnMoodlesStatusManagerModified;
        _clientState.Login             -= OnLogin;
        _clientState.Logout            -= OnLogout;
        _condition.ConditionChange     -= OnConditionChange;
        if (_decals is not null)
            _decals.Completed -= OnDecalPublishCompleted;
        _penumbra.GameObjectRedrawn    -= OnGameObjectRedrawn;

        try
        {
            if (_store.AnythingApplied)
                _cleanup.Cleanup("plugin unload", redrawNow: true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Cleanup on unload failed.");
        }
    }
}
