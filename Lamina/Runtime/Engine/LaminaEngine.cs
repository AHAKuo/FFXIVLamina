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
/// schedules at most one redraw per target. Owns collection detection (read only: the player assigns collections in Penumbra)
/// and the master toggle.
/// <para>
/// One <see cref="TargetSession"/> per target: the local player always, plus one for every other character an enabled profile
/// names (another player by name and home world, or a humanoid NPC). Those sessions apply while the character is rendered near
/// you, re-assert when it comes back, and undo what they can by identity when a profile is switched off while it is away.
/// Conditions are evaluated against your own game state for every target; the target only receives the result.
/// </para>
/// </summary>
public sealed class LaminaEngine : ILaminaEngine, IDisposable
{
    private static readonly TimeSpan ReassertThrottle     = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowTickInterval     = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NamesRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MoodlesOwnCallWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MoodlesEventThrottle = TimeSpan.FromSeconds(1);

    /// <summary> After an apply with problems, wait this long before trying again so a persistent failure does not hammer the IPC every tick. </summary>
    private static readonly TimeSpan ApplyRetryBackoff = TimeSpan.FromSeconds(3);

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
    private readonly TargetResolver _targets;
    private readonly HashSet<Guid> _sweptCollections = new();

    private readonly TargetSession _local;
    private readonly List<TargetSession> _others = new();

    private bool _dirty = true;
    private bool _force;
    private bool _sessionsStale = true;
    private bool _applying;
    private bool _lastHoldState;
    private DateTime _lastReassert     = DateTime.MinValue;
    private DateTime _lastSlowTick     = DateTime.MinValue;
    private DateTime _lastNamesRefresh = DateTime.MinValue;
    private DateTime _lastMoodlesEvent = DateTime.MinValue;
    private DateTime? _timerDue;
    private DateTime? _retryDue;
    private bool _collectionRefreshPending;
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
        _profiles           = profiles;
        _tracker            = tracker;
        _evaluator          = evaluator;
        _compositor         = compositor;
        _resolver           = resolver;
        _cache              = cache;
        _glamourer          = glamourer;
        _penumbra           = penumbra;
        _honorific          = honorific;
        _moodles            = moodles;
        _customizePlus      = customizePlus;
        _heels              = heels;
        _customizePlusCache = customizePlusCache;
        _vitrine            = vitrine;
        _decals             = decalPublisher;
        _meshes             = skinMeshes;
        _framework          = framework;
        _objects            = objects;
        _clientState        = clientState;
        _condition          = condition;
        _catalog            = catalog;
        _log                = log;
        _clock              = clock ?? new TickClock();
        _targets            = new TargetResolver(objects, framework, _catalog.WorldName);

        _local = new TargetSession(Target.LocalPlayer(), _ => LocalActor(), _glamourer, _penumbra, _honorific, _moodles, _customizePlus, _heels,
            _decals, _evaluator, _compositor, _resolver, () => _profiles.Config, _clock.Read, m => _log.Debug(m), DrawObjectKey);
        if (_decals is not null)
            _followUp = new DecalFollowUp(_local.Store, _clock.Read, DrawObject, DecalsReady, () => _decals.Busy, PublishDecals, m => _log.Debug(m));

        _lastHoldState = _profiles.Config.HoldGlamourerState;

        _tracker.SnapshotChanged           += OnSnapshotChanged;
        _profiles.Changed                  += OnProfilesChanged;
        _vitrine.DesignsChanged            += OnVitrineChanged;
        _glamourer.AvailabilityChanged     += OnGlamourerAvailability;
        _penumbra.AvailabilityChanged      += OnPenumbraAvailability;
        _honorific.AvailabilityChanged     += Invalidate;
        _moodles.AvailabilityChanged       += Invalidate;
        _customizePlus.AvailabilityChanged += OnCustomizePlusAvailability;
        _customizePlus.ProfilesChanged     += OnCustomizePlusProfilesChanged;
        _heels.AvailabilityChanged         += OnHeelsAvailability;
        _heels.LocalOffsetChanged          += OnHeelsLocalOffsetChanged;
        _glamourer.StateFinalized          += OnStateFinalized;
        if (_moodles is IMoodlesStatusEvents moodleEvents)
            moodleEvents.StatusManagerModified += OnMoodlesStatusManagerModified;
        _clientState.Login                 += OnLogin;
        _clientState.Logout                += OnLogout;
        _clientState.TerritoryChanged      += OnTerritoryChanged;
        _condition.ConditionChange         += OnConditionChange;
        _framework.Update                  += OnFrameworkUpdate;
        if (_decals is not null)
            _decals.Completed += OnDecalPublishCompleted;
        _penumbra.GameObjectRedrawn        += OnGameObjectRedrawn;

        // Dalamud constructs plugins off the main thread and the object table refuses off-thread access,
        // so the first collection check runs on the first framework tick instead of here.
        _collectionRefreshPending = true;
    }

    /// <summary> Every session, the local player first. </summary>
    private IEnumerable<TargetSession> Sessions
        => _others.Prepend(_local);

    // ---- ILaminaEngine ----------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Enabled
        => _profiles.Config.Enabled;

    /// <inheritdoc/>
    public GameStateSnapshot Snapshot
        => _tracker.Current;

    /// <inheritdoc/>
    public VirtualDesign? LastDesign
        => _local.LastDesign;

    /// <inheritdoc/>
    public EvaluationReport? LastReport
        => _local.LastReport;

    /// <inheritdoc/>
    public ApplyInfo? LastApply
        => _local.LastApply;

    /// <inheritdoc/>
    public bool RedrawPending
        => Sessions.Any(s => s.Redraw.Pending);

    /// <inheritdoc/>
    public CollectionStatus CollectionStatus
        => _local.CollectionStatus;

    /// <inheritdoc/>
    public Guid? CollectionId
        => _local.CollectionId;

    /// <inheritdoc/>
    public event Action? Applied;

    /// <summary> The local player's applied-state store, for the debug overlay. </summary>
    public AppliedStateStore AppliedState
        => _local.Store;

    /// <inheritdoc/>
    public IReadOnlyList<TargetStatus> Targets
        => Sessions.Select(s => new TargetStatus(s.Target, s.Present, s.Actor?.Index, s.Profile?.Name ?? _profiles.ActiveProfileFor(s.Target)?.Name,
            s.CollectionStatus, CollectionNameFor(s), s.Dropped, s.LastApply, s.Shadowed, s.Retired)).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<TargetCandidate> NearbyCandidates()
    {
        try
        {
            return _targets.Candidates();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Listing nearby characters failed.");
            return Array.Empty<TargetCandidate>();
        }
    }

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
        foreach (var session in Sessions)
            RefreshCollectionFor(session);
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

    /// <summary> Forgets what Lamina wrote into the collections (someone else wrote there with Lamina's key) so the next apply rewrites them. </summary>
    public void InvalidateMods()
    {
        foreach (var session in Sessions)
            session.ForgetMods();
        _dirty = true;
    }

    /// <inheritdoc/>
    public bool RedrawNow()
    {
        _local.Redraw.Request();
        return _local.Redraw.RedrawNow();
    }

    /// <inheritdoc/>
    public bool DecalsApplied
        => _local.Store.DecalsApplied;

    /// <inheritdoc/>
    public void ForgetDecals(bool redraw)
    {
        _local.Store.ForgetDecals();
        _local.Store.DesignHash = null;
        _followUp?.Reset();
        if (redraw)
            _local.Redraw.Request();
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
        sb.Append("Lamina: ").Append(Enabled ? "enabled" : "disabled").Append(" · profile: ").Append(profile?.Name ?? "(none)");
        if (_others.Count > 0)
            sb.Append(" · other targets: ").Append(_others.Count);
        sb.AppendLine();
        sb.Append("Collection: ").Append(_profiles.Config.CollectionName).Append(" · ").AppendLine(DescribeCollection(_local.CollectionStatus, local: true));
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
        AppendApply(sb, _local.LastApply);
        if (_local.Redraw.Pending)
            sb.AppendLine("Redraw pending.");

        if (_local.LastDesign is { } design && profile is not null)
            sb.AppendLine(Explain(design, profile, _local.LastReport));
        else
            sb.AppendLine("No design evaluated yet.");

        foreach (var other in _others)
        {
            sb.Append("Target ").Append(DescribeTarget(other.Target)).Append(": ");
            var active = _profiles.ActiveProfileFor(other.Target);
            sb.Append("profile ").Append(active?.Name ?? "(none)");
            if (other.Retired) sb.Append(" (removed, undoing)");
            if (other.Shadowed) sb.Append(" · shadowed by your Local Player profile");
            sb.Append(other.Present ? $" · rendered at #{other.Actor?.Index}" : " · not rendered right now");
            var collection = CollectionNameFor(other);
            sb.Append(" · collection ").Append(collection is null ? "(none)" : $"\"{collection}\"").Append(' ').Append(DescribeCollection(other.CollectionStatus, local: false));
            sb.AppendLine();
            foreach (var dropped in other.Dropped)
                sb.Append("  · ").AppendLine(dropped);
            AppendApply(sb, other.LastApply, "  ");
            if (other.LastDesign is { } otherDesign && active is not null && !otherDesign.IsEmpty)
                sb.AppendLine(Explain(otherDesign, active, other.LastReport));
        }

        return sb.ToString().TrimEnd();
    }

    private string Explain(VirtualDesign design, Profile profile, EvaluationReport? report)
    {
        var groups = profile.PassGroups.ToDictionary(g => g.Id, g => g.Name);
        var passes = profile.PassGroups.SelectMany(g => g.Passes).ToDictionary(p => p.Id, p => p.Name);
        return DesignExplainer.Explain(design,
            id => groups.TryGetValue(id, out var n) ? n : id.ToString(),
            id => passes.TryGetValue(id, out var n) ? n : id.ToString(),
            report?.SkippedLayers);
    }

    private static void AppendApply(StringBuilder sb, ApplyInfo? apply, string indent = "")
    {
        if (apply is null)
            return;

        sb.Append(indent).Append("Last apply: ").Append(apply.AtUtc.ToLocalTime().ToString("HH:mm:ss")).Append(" · glamourer ").Append(apply.GlamourerApplied)
          .Append(" · mods ").Append(apply.ModsWritten).Append(" · decals ").Append(apply.DecalsPublished ? "published" : apply.DecalsDeferred ? "pending redraw" : "none")
          .Append(" · redraw ").Append(apply.RedrawRequested ? (apply.RedrawDeferred ? "deferred" : "yes") : "no");
        if (apply.Problems.Count > 0)
            sb.Append(" · problems: ").Append(string.Join("; ", apply.Problems));
        sb.AppendLine();
    }

    private static string DescribeCollection(CollectionStatus status, bool local)
        => status switch
        {
            CollectionStatus.PenumbraMissing => "Penumbra missing",
            CollectionStatus.NotFound        => "not found in Penumbra (create it there)",
            CollectionStatus.NotConfigured   => "no collection chosen (mods skipped)",
            CollectionStatus.NotAssigned     => local
                ? "not assigned to your character in Penumbra (mods skipped)"
                : "mods written, but the character is not assigned to it in Penumbra (they do not show on them)",
            _                                => local ? "assigned to your character" : "assigned to that character",
        };

    private string DescribeTarget(Target target)
        => target.Kind switch
        {
            TargetKind.LocalPlayer => "Local Player",
            TargetKind.OtherPlayer => $"{target.Name}@{_catalog.WorldName(target.WorldId ?? 0)}",
            _                      => $"{target.Name} (NPC)",
        };

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
                SyncSessions();
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

            if (_retryDue is { } retry && now >= retry)
            {
                _retryDue = null;
                _dirty    = true;
            }

            if (now - _lastReassert >= ReassertThrottle)
            {
                foreach (var session in Sessions)
                {
                    if (!session.ReassertGlamourer)
                        continue;

                    // Glamourer automation wrote over Lamina's state: forget only the Glamourer section so the normal diff
                    // re-applies it. Never a forced full apply (that would clear and rewrite every mod and redraw).
                    session.ReassertGlamourer = false;
                    _lastReassert             = now;
                    session.ForgetGlamourer();
                    _dirty = true;
                }
            }

            CheckHoldStateFlip();

            if (now - _lastSlowTick >= SlowTickInterval)
            {
                _lastSlowTick = now;
                SlowTick(now);
            }

            if (_dirty)
                Evaluate(now);

            var policy = _profiles.Config.RedrawPolicy;
            foreach (var session in Sessions)
            {
                if (session.Redraw.Tick(_tracker.Current, policy))
                    _log.Information(session.Target.IsLocal ? "Redraw sent to Penumbra." : $"Redraw of {DescribeTarget(session.Target)} sent to Penumbra.");
                else if (session.Redraw.Pending && _tracker.Current.RedrawUnsafe)
                    _log.Debug("Redraw deferred: unsafe right now.");
            }

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
        // Opt-in / opt-out detection: the player may (un)assign a collection in Penumbra at any time.
        if (_penumbra.Available)
        {
            foreach (var session in Sessions)
            {
                if (session.CollectionStatus is CollectionStatus.PenumbraMissing or CollectionStatus.NotFound or CollectionStatus.NotConfigured)
                {
                    RefreshCollectionFor(session);
                }
                else if (session.CollectionId is not null && session.Refresh(now) is { } actor)
                {
                    var previous = session.CollectionStatus;
                    session.CollectionStatus = ReadAssignment(session, actor.Index);
                    OnCollectionStatusChanged(session, previous);
                }
            }
        }

        // Characters come and go without any event: notice within a second.
        foreach (var session in Sessions)
        {
            var was = session.Present;
            session.Refresh(now);
            if (was != session.Present)
                _dirty = true;
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
        foreach (var session in Sessions)
        {
            if (!session.Store.GlamourerApplied)
                continue;

            if (!hold && _glamourer is IGlamourerUnlock unlock && session.Index() is { } index)
            {
                var code = unlock.Unlock(index);
                _log.Debug($"Hold Glamourer state switched off: unlock of {DescribeTarget(session.Target)} returned {code}.");
            }

            // Re-apply so the state carries (or drops) the lock.
            session.ForgetGlamourer();
            _dirty = true;
        }
    }

    /// <summary>
    /// Keeps one session per non-local target an enabled profile names. Sessions whose target no profile wants any more are
    /// retired: they keep running with no profile until everything Lamina applied is undone, then they are dropped.
    /// </summary>
    private void SyncSessions()
    {
        _sessionsStale = false;
        var wanted = _profiles.Profiles.Where(p => p.Enabled).SelectMany(p => p.Targets).Where(t => !t.IsLocal).Distinct().ToList();
        foreach (var target in wanted)
        {
            var session = _others.FirstOrDefault(o => o.Target.Equals(target));
            if (session is null)
            {
                session = CreateSession(target);
                _others.Add(session);
                RefreshCollectionFor(session);
                _log.Information($"Target added: {DescribeTarget(target)}.");
            }
            else
            {
                session.Retired = false;
            }
        }

        foreach (var session in _others)
        {
            if (session.Retired || wanted.Contains(session.Target))
                continue;
            session.Retired = true;
            _log.Information($"Target removed: {DescribeTarget(session.Target)}; undoing what Lamina applied.");
        }
    }

    private TargetSession CreateSession(Target target)
    {
        var copy = new Target { Kind = target.Kind, Name = target.Name, WorldId = target.WorldId, DataId = target.DataId };
        return new TargetSession(copy, now => _targets.IsLocalIdentity(copy) ? LocalActor() : _targets.Locate(copy, now),
            _glamourer, _penumbra, _honorific, _moodles, _customizePlus, _heels, decals: null, _evaluator, _compositor, _resolver,
            () => _profiles.Config, _clock.Read, m => _log.Debug($"[{DescribeTarget(copy)}] {m}"));
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
        if (!_clientState.IsLoggedIn || _local.Refresh(now) is null)
            return;

        if (_sessionsStale)
            SyncSessions();

        var snapshot = _tracker.Current;
        var force    = _force;
        _dirty = false;
        _force = false;

        var matched    = new HashSet<Guid>();
        var anyApplied = false;
        var localProfile = _profiles.ActiveProfileFor(Target.LocalPlayer());
        foreach (var session in Sessions.ToList())
        {
            var profile = session.Retired ? null : session.Target.IsLocal ? localProfile : _profiles.ActiveProfileFor(session.Target);
            anyApplied |= StepSession(session, snapshot, profile, localProfile, force, now, matched);
        }

        // Random picks are kept for every pass that contributes to any target.
        _compositor.RandomPicks.Retain(matched);

        // Retired sessions leave once nothing of Lamina's is left on their character.
        var dropped = _others.RemoveAll(o => o.Retired && !o.Store.AnythingApplied);
        if (dropped > 0)
            _log.Debug($"{dropped} retired target session(s) dropped.");

        if (anyApplied)
            Applied?.Invoke();

        _timerDue = _evaluator.TimeUntilNextChange is { } wait ? now + wait : null;
        var retries = Sessions.Select(s => s.RetryDue).Where(r => r is not null).ToList();
        _retryDue = retries.Count > 0 ? retries.Min() : null;
    }

    /// <summary> Evaluates one target. Returns true when something was applied or cleaned up (the Applied event is due). </summary>
    private bool StepSession(TargetSession session, GameStateSnapshot snapshot, Profile? profile, Profile? localProfile, bool force, DateTime now, HashSet<Guid> matched)
    {
        // A named player target that is you yields to a Local Player profile; two profiles must never fight over your character.
        session.Shadowed = !session.Target.IsLocal && localProfile is not null && _targets.IsLocalIdentity(session.Target);
        if (session.Shadowed)
            profile = null;

        var actor = session.Target.IsLocal ? session.Actor : session.Refresh(now);
        if (actor is null)
        {
            if (session.WasPresent)
            {
                session.WasPresent        = false;
                session.AbsentCleanupDone = false;
                session.Redraw.Cancel();
            }

            session.Profile = profile;
            if (profile is null && session.Store.AnythingApplied && !session.AbsentCleanupDone)
            {
                // Nothing to undo on an actor that is not there; take back what the plugins hold by identity and remember the rest.
                session.AbsentCleanupDone = true;
                _applying = true;
                try
                {
                    session.Cleanup.CleanupAbsent(session.Target.Kind == TargetKind.OtherPlayer ? session.Target.Name : null,
                        session.Retired ? "target removed" : "profile disabled");
                }
                finally
                {
                    _applying = false;
                }

                session.ForgetOutcome();
                return true;
            }

            return false;
        }

        if (!session.WasPresent)
        {
            // Back on screen: re-assert everything Lamina holds for it (the plugins may or may not have kept it).
            session.WasPresent        = true;
            session.AbsentCleanupDone = false;
            if (session.Store.AnythingApplied || profile is not null)
                force = true;
            if (!session.Target.IsLocal)
                RefreshCollectionFor(session);
        }

        force |= session.Force;
        session.Force = false;

        // Back off after a failed apply (the engine wakes up when it is due); a forced reapply skips the wait.
        if (!force && session.RetryDue is { } retryDue && now < retryDue)
            return false;

        session.Profile = profile;
        EngineStep step;
        _applying = true;
        try
        {
            step = session.Core.Step(snapshot, profile, force);
        }
        finally
        {
            _applying = false;
        }

        session.LastDesign = step.Design;
        session.LastReport = step.Report;
        session.Dropped    = step.Dropped;
        if (step.Report is { } report)
            foreach (var id in report.MatchedPassIds)
                matched.Add(id);

        if (step.Apply is { } apply)
        {
            session.LastApply = apply;
            var who = session.Target.IsLocal ? string.Empty : $"[{DescribeTarget(session.Target)}] ";
            if (apply.Problems.Count > 0)
            {
                _log.Warning($"{who}Apply problems: {string.Join("; ", apply.Problems)}");
                session.RetryDue = now + ApplyRetryBackoff;
            }
            else
            {
                _log.Debug($"{who}Applied {apply.DesignHash}: glamourer {apply.GlamourerApplied}, mods {apply.ModsWritten}, redraw {apply.RedrawRequested}"
                         + (step.Dropped.Count > 0 ? $"; dropped: {string.Join("; ", step.Dropped)}" : string.Empty));
                session.RetryDue = null;
            }

            return true;
        }

        return step.ProfileSwitched;
    }

    // ---- collection -------------------------------------------------------------------------------

    /// <summary>
    /// The collection a session's mods go into: the Settings collection for the local player, and for another target the collection
    /// named on it (taken from the enabled profile that names it) or, when none is, that same Settings collection.
    /// </summary>
    private string? CollectionNameFor(TargetSession session)
    {
        var name = _profiles.Config.CollectionName?.Trim();
        if (!session.Target.IsLocal)
        {
            var instance = _profiles.Profiles.Where(p => p.Enabled).SelectMany(p => p.Targets).FirstOrDefault(t => t.Equals(session.Target))
                        ?? _profiles.Profiles.SelectMany(p => p.Targets).FirstOrDefault(t => t.Equals(session.Target));
            var own = instance?.CollectionName?.Trim();
            if (!string.IsNullOrEmpty(own))
                name = own;
        }

        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void RefreshCollectionFor(TargetSession session)
    {
        var previous   = session.CollectionStatus;
        var previousId = session.CollectionId;
        var name       = CollectionNameFor(session);
        if (!_penumbra.Available)
        {
            session.CollectionId     = null;
            session.CollectionStatus = CollectionStatus.PenumbraMissing;
        }
        else if (name is null)
        {
            session.CollectionId     = null;
            session.CollectionStatus = CollectionStatus.NotConfigured;
        }
        else
        {
            session.CollectionId = _penumbra.FindCollection(name);
            if (session.CollectionId is null)
                session.CollectionStatus = CollectionStatus.NotFound;
            else if ((session.Target.IsLocal ? LocalActor() : session.Refresh(_clock.Now)) is { } actor)
                session.CollectionStatus = ReadAssignment(session, actor.Index);
            else
                session.CollectionStatus = previous is CollectionStatus.PenumbraMissing or CollectionStatus.NotFound or CollectionStatus.NotConfigured
                    ? CollectionStatus.NotAssigned
                    : previous;
        }

        // The collection moved (renamed in Settings or on the target): take this target's mods out of the old one.
        if (previousId is { } old && previousId != session.CollectionId && _penumbra.Available && (session.Store.ModsApplied || session.Store.DecalsApplied))
        {
            var problems = new List<string>();
            TemporaryModSettings.RemoveWritten(_penumbra, old, session.Store, problems);
            foreach (var problem in problems)
                _log.Warning($"Could not clear Lamina's settings from the previous collection: {problem}");
            if (session.Store.DecalsApplied)
                _decals?.Clear(old);
            session.ForgetMods();
            _dirty = true;
        }

        if (session.CollectionId is { } found)
            SweepOnce(found);

        OnCollectionStatusChanged(session, previous);
    }

    /// <summary>
    /// Once per collection and plugin lifetime, before any target writes there: removes temporary settings an earlier Lamina
    /// instance left behind (crash, hot reload). Every later removal is per mod, since targets may share the collection.
    /// </summary>
    private void SweepOnce(Guid collection)
    {
        if (!_sweptCollections.Add(collection))
            return;
        if (Sessions.Any(s => s.CollectionId == collection && (s.Store.ModsApplied || s.Store.DecalsApplied)))
            return;

        var code = _penumbra.RemoveAllTemporaryModSettings(collection);
        if (code is PenumbraApiEc.Success)
            _log.Information("Removed temporary settings an earlier Lamina instance left in the collection.");
        else if (code is not PenumbraApiEc.NothingChanged)
            _log.Warning($"Could not sweep the collection before first use: {code}");
    }

    private CollectionStatus ReadAssignment(TargetSession session, int index)
    {
        var (valid, _, effective, _) = _penumbra.GetCollectionForObject(index);
        return valid && session.CollectionId is { } collection && effective == collection ? CollectionStatus.Assigned : CollectionStatus.NotAssigned;
    }

    private void OnCollectionStatusChanged(TargetSession session, CollectionStatus previous)
    {
        if (session.CollectionStatus == CollectionStatus.Assigned)
            session.LastAssignedCollection = session.CollectionId;

        if (previous == session.CollectionStatus)
            return;

        var who = session.Target.IsLocal ? "your character" : DescribeTarget(session.Target);
        _log.Debug($"Collection status ({who}): {previous} → {session.CollectionStatus}");

        if (!session.Target.IsLocal)
        {
            // Another target's mods are written whether or not the character is assigned; the status only says whether they
            // show on it. Rewrite on the next evaluation so a newly found (or newly assigned) collection gets them, with a redraw.
            session.Store.ModsHash   = null;
            session.Store.DesignHash = null;
            _dirty = true;
            return;
        }

        if (previous == CollectionStatus.Assigned)
        {
            // Opt out: the player removed the assignment (or the collection vanished). Leave nothing of Lamina's behind.
            if (_penumbra.Available && session.LastAssignedCollection is { } old)
            {
                var problems = new List<string>();
                TemporaryModSettings.RemoveWritten(_penumbra, old, session.Store, problems);
                foreach (var problem in problems)
                    _log.Warning($"Could not clear Lamina's settings from the previous collection: {problem}");
                if (session.Store.DecalsApplied)
                    _decals?.Clear(old);
            }

            session.ForgetMods();
            if (session.Target.IsLocal)
                _followUp?.Reset();
            _log.Information($"The Lamina collection is no longer assigned to {who}; mods are skipped until it is assigned again in Penumbra.");
        }
        else if (session.CollectionStatus == CollectionStatus.Assigned)
        {
            // Opt in: write the full mod state on the next evaluation.
            session.Store.ModsHash   = null;
            session.Store.DesignHash = null;
        }

        _dirty = true;
    }

    // ---- events -----------------------------------------------------------------------------------

    private void OnSnapshotChanged(GameStateSnapshot snapshot)
        => _dirty = true;

    private void OnProfilesChanged()
    {
        _sessionsStale = true;
        _dirty         = true;
    }

    private void OnVitrineChanged()
        => _dirty = true;

    private void OnTerritoryChanged(uint territory)
    {
        // Every other character is gone; the resolver would notice per call, but a clean slate is cheaper.
        _targets.Clear();
        _dirty = true;
    }

    private void OnGlamourerAvailability()
    {
        _cache.InvalidateAll();
        if (_glamourer.Available)
        {
            RequestReapply();
        }
        else
        {
            foreach (var session in Sessions)
                session.ForgetGlamourer();
        }
    }

    private void OnCustomizePlusAvailability()
    {
        _customizePlusCache.InvalidateAll();
        foreach (var session in Sessions)
        {
            if (!_customizePlus.Available)
            {
                // The temporary profile died with the plugin; nothing to undo, but the next apply must set it again.
                session.Store.CustomizePlusHash        = null;
                session.Store.CustomizePlusTemporaryId = null;
            }

            // Either way the design hash no longer tells the whole story (the section was skipped or lost): diff again.
            session.InvalidateDesign();
        }

        _dirty = true;
    }

    /// <summary> Customize+ reported a profile change: its bones may differ, so the cached profiles are dropped and the design re-evaluated. </summary>
    private void OnCustomizePlusProfilesChanged()
    {
        _customizePlusCache.InvalidateAll();
        _dirty = true;
    }

    private void OnHeelsAvailability()
    {
        foreach (var session in Sessions)
        {
            if (!_heels.Available)
            {
                // Simple Heels forgets IPC registrations when it unloads; re-register on the next apply.
                session.Store.HeelsSet  = null;
                session.Store.HeelsBase = null;
            }

            session.InvalidateDesign();
        }

        _dirty = true;
    }

    /// <summary>
    /// The player's own Simple Heels offset changed (footwear swap, edited configuration). A relative registration was built on the
    /// old value, so it is forgotten and the next evaluation registers again on the new base. Absolute registrations (and every
    /// other target, whose offsets are always absolute) are unaffected.
    /// </summary>
    private void OnHeelsLocalOffsetChanged(float offset)
    {
        var store = _local.Store;
        if (store.HeelsSet is not { Relative: true } || store.HeelsBase is not { } baseOffset || MathF.Abs(baseOffset - offset) < 0.0001f)
            return;

        _log.Debug($"Simple Heels: player offset changed {baseOffset:0.###} → {offset:0.###}; re-registering the relative offset.");
        store.HeelsSet   = null;
        store.HeelsBase  = null;
        store.DesignHash = null;
        _dirty           = true;
    }

    private void OnPenumbraAvailability()
    {
        if (!_penumbra.Available)
        {
            foreach (var session in Sessions)
            {
                session.ForgetMods();
                session.Redraw.Cancel();
            }

            _followUp?.Reset();
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
        if (_applying || address == 0)
            return;

        var session = SessionAt(address);
        if (session is null || !session.Store.GlamourerApplied)
            return;

        _log.Debug($"Glamourer finalized {type} on {DescribeTarget(session.Target)}; re-asserting Lamina's state.");
        session.ReassertGlamourer = true;
    }

    private void OnMoodlesStatusManagerModified(nint address)
    {
        if (address == 0)
            return;

        var session = SessionAt(address);
        if (session is null)
            return;

        var store = session.Store;
        if (store.MoodlesAdded.Count == 0 && store.PresetsApplied.Count == 0)
            return;

        var now = _clock.Now;
        if (store.LastMoodlesCallUtc is { } own && now - own < MoodlesOwnCallWindow)
            return;
        if (now - _lastMoodlesEvent < MoodlesEventThrottle)
            return;

        // Something else changed the status manager (a moodle expired or was removed): forget what Lamina added so the
        // next evaluation re-adds whatever the design still wants.
        _lastMoodlesEvent = now;
        store.MoodlesAdded.Clear();
        store.PresetsApplied.Clear();
        store.DesignHash = null;
        _dirty           = true;
    }

    /// <summary> The session whose character sits at this address, by the last known location (no table scan on the event path). </summary>
    private TargetSession? SessionAt(nint address)
    {
        if (LocalActor() is { } local && local.Address == address)
            return _local;
        return _others.FirstOrDefault(o => o.Actor?.Address == address);
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
            _local.Redraw.Request();
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
        _local.Store.ForgetDecals();
        _local.Store.DesignHash = null;
        _local.RetryDue         = _clock.Now + ApplyRetryBackoff;
        _retryDue               = _local.RetryDue;
    }

    private void OnGameObjectRedrawn(nint address, int objectIndex)
    {
        if (_disposed || _followUp is null)
            return;

        // Penumbra raises this on the framework thread for every object it redraws (ours or another plugin's).
        if (LocalActor() is { } local && objectIndex == local.Index)
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
        if (flag == ConditionFlag.LoggingOut && value && Sessions.Any(s => s.Store.AnythingApplied))
            RunCleanup("logging out", redrawNow: false);
    }

    private void OnLogout(int type, int code)
    {
        if (Sessions.Any(s => s.Store.AnythingApplied))
            RunCleanup("logout", redrawNow: false);

        foreach (var session in Sessions)
        {
            session.ForgetOutcome();
            session.Redraw.Cancel();
            session.WasPresent = false;
        }

        _others.Clear();
        _sessionsStale = true;
        _targets.Clear();
        _followUp?.Reset();
    }

    /// <summary> Cleans up every target: the full cleanup where the character is rendered, the identity-based one where it is not. </summary>
    private void RunCleanup(string reason, bool redrawNow)
    {
        _applying = true;
        try
        {
            foreach (var session in Sessions.ToList())
            {
                if (session.Target.IsLocal)
                {
                    // Always, even with nothing recorded: the local cleanup also resets the condition timers and gives the collection a clean slate.
                    session.Cleanup.Cleanup(reason, redrawNow);
                    continue;
                }

                if (!session.Store.AnythingApplied)
                    continue;

                if (session.Refresh(_clock.Now) is not null)
                    session.Cleanup.Cleanup(reason, redrawNow);
                else
                    session.Cleanup.CleanupAbsent(session.Target.Kind == TargetKind.OtherPlayer ? session.Target.Name : null, reason);
            }
        }
        finally
        {
            _applying = false;
        }

        foreach (var session in Sessions)
            session.ForgetOutcome();
        _others.RemoveAll(o => o.Retired && !o.Store.AnythingApplied);
        _followUp?.Reset();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary> The object table only answers on the framework thread; off-thread callers see "no player". </summary>
    private ResolvedActor? LocalActor()
        => _framework.IsInFrameworkUpdateThread && _objects.LocalPlayer is { } player ? new ResolvedActor(player.ObjectIndex, player.Address) : null;

    private DrawObjectIdentity? DrawObject()
        => _framework.IsInFrameworkUpdateThread ? _meshes?.DrawObjectState() : null;

    private string? DrawObjectKey()
        => DrawObject()?.Key;

    /// <summary> A decal publish can go out: Lamina on, Penumbra there, the collection assigned, a player to draw. </summary>
    private bool DecalsReady()
        => Enabled && _penumbra.Available && _local.CollectionStatus == CollectionStatus.Assigned && _local.CollectionId is not null && LocalActor() is not null;

    private void PublishDecals(IReadOnlyList<DecalSpec> decals)
    {
        if (_decals is not null && _local.CollectionId is { } collection && LocalActor() is { } local)
            _decals.Publish(collection, local.Index, decals, _local.Store.ModsHash);
    }

    private static string Flag(string name, bool on)
        => on ? name : $"{name} (missing)";

    /// <summary> Runs the unload cleanup and unsubscribes. Safe to call twice. </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _framework.Update                  -= OnFrameworkUpdate;
        _tracker.SnapshotChanged           -= OnSnapshotChanged;
        _profiles.Changed                  -= OnProfilesChanged;
        _vitrine.DesignsChanged            -= OnVitrineChanged;
        _glamourer.AvailabilityChanged     -= OnGlamourerAvailability;
        _penumbra.AvailabilityChanged      -= OnPenumbraAvailability;
        _honorific.AvailabilityChanged     -= Invalidate;
        _moodles.AvailabilityChanged       -= Invalidate;
        _customizePlus.AvailabilityChanged -= OnCustomizePlusAvailability;
        _customizePlus.ProfilesChanged     -= OnCustomizePlusProfilesChanged;
        _heels.AvailabilityChanged         -= OnHeelsAvailability;
        _heels.LocalOffsetChanged          -= OnHeelsLocalOffsetChanged;
        _glamourer.StateFinalized          -= OnStateFinalized;
        if (_moodles is IMoodlesStatusEvents moodleEvents)
            moodleEvents.StatusManagerModified -= OnMoodlesStatusManagerModified;
        _clientState.Login                 -= OnLogin;
        _clientState.Logout                -= OnLogout;
        _clientState.TerritoryChanged      -= OnTerritoryChanged;
        _condition.ConditionChange         -= OnConditionChange;
        if (_decals is not null)
            _decals.Completed -= OnDecalPublishCompleted;
        _penumbra.GameObjectRedrawn        -= OnGameObjectRedrawn;

        try
        {
            if (Sessions.Any(s => s.Store.AnythingApplied))
                RunCleanup("plugin unload", redrawNow: true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Cleanup on unload failed.");
        }
    }
}
