using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lamina.Models;
using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Lamina.Runtime.State;

/// <summary>
/// Reads the game state for the local player and publishes a new <see cref="GameStateSnapshot"/> only when something
/// conditions can observe has changed. Listens to Dalamud events (which force an immediate rebuild on the next tick)
/// and polls on <see cref="IFramework.Update"/>: cheap fields at ~10 Hz, slower ones (weather, Eorzea time, party,
/// status list, housing) at ~1 Hz. Every section is guarded so one failing read never prevents a snapshot.
/// </summary>
public sealed class StateTracker : IDisposable
{
    private const int FastIntervalMs = 100;
    private const int SlowIntervalMs = 1000;

    /// <summary> Sprint status id. </summary>
    private const uint SprintStatusId = 50;

    /// <summary> Below this speed (yalms per second) the player counts as idle. </summary>
    private const float IdleSpeed = 0.2f;

    /// <summary> Above this speed the player counts as running rather than walking (walk ≈ 2.0, run ≈ 6.0). </summary>
    private const float RunSpeed = 4.0f;

    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IPartyList _party;
    private readonly IDutyState _duty;
    private readonly IPlayerState _playerState;
    private readonly IFateTable _fates;
    private readonly IPluginLog _log;
    private readonly GameCatalog _catalog;
    private readonly Func<BiomeMap> _biomes;

    private readonly HashSet<string> _warned = new();

    private long _sequence;
    private DateTime _lastFastUtc = DateTime.MinValue;
    private DateTime _lastSlowUtc = DateTime.MinValue;
    private bool _forceRebuild = true;

    private Vector3? _lastPosition;
    private DateTime _lastPositionUtc;
    private float _speed;

    private SlowFields _slow = new();

    private sealed class SlowFields
    {
        public byte WeatherId;
        public IReadOnlySet<WeatherGroup> WeatherGroups = new HashSet<WeatherGroup>();
        public TimeSpan EorzeaTime;
        public IReadOnlyList<StatusEntry> Statuses = Array.Empty<StatusEntry>();
        public int PartySize = 1;
        public bool InAlliance;
        public IReadOnlyList<string> PartyMembers = Array.Empty<string>();
        public HousingInfo? Housing;
        public bool IsSanctuary;
        public bool IsIndoors;
        public bool InFate;
        public int GearsetId = -1;
        public IReadOnlyList<string> ActiveEvents = Array.Empty<string>();
    }

    /// <summary> The most recent snapshot. Never null; <see cref="GameStateSnapshot.Empty"/> before login. </summary>
    public GameStateSnapshot Current { get; private set; } = GameStateSnapshot.Empty;

    /// <summary> Raised on the framework thread with every new snapshot. </summary>
    public event Action<GameStateSnapshot>? SnapshotChanged;

    public StateTracker(
        IClientState clientState,
        ICondition condition,
        IFramework framework,
        IObjectTable objects,
        IPartyList party,
        IDutyState duty,
        IPlayerState playerState,
        IFateTable fates,
        IPluginLog log,
        GameCatalog catalog,
        Func<BiomeMap> biomes)
    {
        _clientState = clientState;
        _condition   = condition;
        _framework   = framework;
        _objects     = objects;
        _party       = party;
        _duty        = duty;
        _playerState = playerState;
        _fates       = fates;
        _log         = log;
        _catalog     = catalog;
        _biomes      = biomes;

        _clientState.Login            += OnLogin;
        _clientState.Logout           += OnLogout;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.ClassJobChanged  += OnClassJobChanged;
        _clientState.LevelChanged     += OnLevelChanged;
        _condition.ConditionChange    += OnConditionChange;
        _duty.DutyStarted             += OnDutyEvent;
        _duty.DutyCompleted           += OnDutyEvent;
        _framework.Update             += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update             -= OnFrameworkUpdate;
        _clientState.Login            -= OnLogin;
        _clientState.Logout           -= OnLogout;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.ClassJobChanged  -= OnClassJobChanged;
        _clientState.LevelChanged     -= OnLevelChanged;
        _condition.ConditionChange    -= OnConditionChange;
        _duty.DutyStarted             -= OnDutyEvent;
        _duty.DutyCompleted           -= OnDutyEvent;
    }

    /// <summary> Forces a full rebuild (including slow fields) on the next framework tick. </summary>
    public void Invalidate()
        => _forceRebuild = true;

    // ---- events ---------------------------------------------------------------------------------

    private void OnLogin()
        => _forceRebuild = true;

    private void OnLogout(int type, int code)
    {
        _forceRebuild = true;
        Publish(GameStateSnapshot.Empty with { Sequence = ++_sequence, BuiltAtUtc = DateTime.UtcNow });
    }

    private void OnTerritoryChanged(uint territory)
        => _forceRebuild = true;

    private void OnClassJobChanged(uint classJob)
        => _forceRebuild = true;

    private void OnLevelChanged(uint classJob, uint level)
        => _forceRebuild = true;

    private void OnConditionChange(ConditionFlag flag, bool value)
        => _forceRebuild = true;

    private void OnDutyEvent(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => _forceRebuild = true;

    // ---- polling --------------------------------------------------------------------------------

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        var fastDue = (now - _lastFastUtc).TotalMilliseconds >= FastIntervalMs;
        if (!_forceRebuild && !fastDue)
            return;

        try
        {
            if (!_clientState.IsLoggedIn || _objects.LocalPlayer is not { } player)
            {
                if (Current.LoggedIn)
                    Publish(GameStateSnapshot.Empty with { Sequence = ++_sequence, BuiltAtUtc = now });
                _forceRebuild = false;
                _lastFastUtc  = now;
                return;
            }

            var slowDue = _forceRebuild || (now - _lastSlowUtc).TotalMilliseconds >= SlowIntervalMs;
            if (slowDue)
            {
                _slow        = ReadSlowFields(player, now);
                _lastSlowUtc = now;
            }

            var candidate = Build(player, now);
            _forceRebuild = false;
            _lastFastUtc  = now;

            if (!SnapshotComparer.Equivalent(Current, candidate))
                Publish(candidate with { Sequence = ++_sequence });
        }
        catch (Exception ex)
        {
            _forceRebuild = false;
            _lastFastUtc  = now;
            WarnOnce("tick", ex, "Lamina: StateTracker tick failed.");
        }
    }

    private void Publish(GameStateSnapshot snapshot)
    {
        Current = snapshot;
        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: SnapshotChanged handler threw.");
        }
    }

    // ---- building -------------------------------------------------------------------------------

    private unsafe GameStateSnapshot Build(IPlayerCharacter player, DateTime nowUtc)
    {
        var flags = _condition.AsReadOnlySet();
        var raw   = new HashSet<int>(flags.Select(f => (int)f));
        var chara = (Character*)player.Address;

        // identity
        var name      = player.Name.TextValue;
        var homeWorld = player.HomeWorld.RowId;
        var curWorld  = player.CurrentWorld.RowId;
        byte race = 0, clan = 0, gender = 0;
        Guard("identity", () =>
        {
            if (_playerState.IsLoaded)
            {
                race   = (byte)_playerState.Race.RowId;
                clan   = (byte)_playerState.Tribe.RowId;
                gender = (byte)_playerState.Sex;
            }
            else
            {
                var cust = player.Customize;
                if (cust.Length > 4)
                {
                    race   = cust[0];
                    gender = cust[1];
                    clan   = cust[4];
                }
            }
        });

        // place
        var territoryId = (uint)_clientState.TerritoryType;
        var territory   = _catalog.Territory(territoryId);
        var biomeMap    = _biomes();
        var biome = biomeMap.TerritoryToBiome.TryGetValue(territoryId, out var overrideBiome) && !string.IsNullOrWhiteSpace(overrideBiome)
            ? overrideBiome
            : GameCatalog.DefaultBiome(territoryId);

        // content
        var inDuty = flags.Contains(ConditionFlag.BoundByDuty) || flags.Contains(ConditionFlag.BoundByDuty56) || flags.Contains(ConditionFlag.BoundByDuty95);
        ushort cfcId = 0;
        Guard("content", () => cfcId = GameMain.Instance()->CurrentContentFinderConditionId);
        if (cfcId == 0)
            cfcId = (ushort)_duty.ContentFinderCondition.RowId;
        var contentKind = inDuty ? (_clientState.IsPvP ? ContentKind.PvP : _catalog.ContentKindOf(cfcId)) : ContentKind.Other;

        // job
        var jobId = player.ClassJob.RowId;
        var job   = _catalog.Job(jobId);
        var weaponDrawn = false;
        Guard("weapon", () => weaponDrawn = chara->IsWeaponDrawn);

        // body / movement / activity
        var movement   = new HashSet<MovementState>();
        var body       = new HashSet<BodyState>();
        var activities = new HashSet<ActivityKind>();
        ushort emoteId = 0;
        var emoteLooping = false;
        PoseKind? pose = null;

        Guard("movement", () =>
        {
            var mounted = flags.Contains(ConditionFlag.Mounted) || chara->IsMounted();
            if (mounted) movement.Add(MovementState.Mounted);
            if (flags.Contains(ConditionFlag.RidingPillion)) movement.Add(MovementState.RidingPillion);
            ref var mc = ref chara->MoveController;
            var state = mc.MovementState;
            if (flags.Contains(ConditionFlag.InFlight) || state == MovementStateOptions.Flying || mc.IsFlying()) movement.Add(MovementState.Flying);
            if (flags.Contains(ConditionFlag.Diving) || state == MovementStateOptions.Diving || mc.IsDiving()) movement.Add(MovementState.Diving);
            else if (flags.Contains(ConditionFlag.Swimming) || mc.IsSwimming) movement.Add(MovementState.Swimming);
            if (flags.Contains(ConditionFlag.Jumping) || flags.Contains(ConditionFlag.Jumping61) || chara->IsJumping()) movement.Add(MovementState.Jumping);
            if (flags.Contains(ConditionFlag.BeingMoved)) movement.Add(MovementState.BeingMoved);

            UpdateSpeed(player.Position, nowUtc);
            if (!mounted && _speed >= IdleSpeed)
            {
                var sprinting = _slow.Statuses.Any(s => s.StatusId == SprintStatusId);
                if (_speed >= RunSpeed)
                    movement.Add(sprinting ? MovementState.Sprinting : MovementState.Running);
                else
                    movement.Add(MovementState.Walking);
            }
            else if (!mounted && movement.Count == 0)
            {
                movement.Add(MovementState.Idle);
            }
        });

        Guard("body", () =>
        {
            if (player.CurrentHp == 0 || flags.Contains(ConditionFlag.Unconscious) || chara->IsDead()) body.Add(BodyState.Dead);
            if (flags.Contains(ConditionFlag.InCombat)) body.Add(BodyState.InCombat);
            if (flags.Contains(ConditionFlag.Casting) || flags.Contains(ConditionFlag.Casting87) || player.IsCasting) body.Add(BodyState.Casting);
            if (flags.Contains(ConditionFlag.Transformed) || chara->TransformationId != 0) body.Add(BodyState.Transformed);
            if (flags.Contains(ConditionFlag.Disguised)) body.Add(BodyState.Disguised);
            if (flags.Contains(ConditionFlag.BetweenAreas) || flags.Contains(ConditionFlag.BetweenAreas51)) body.Add(BodyState.Loading);
            if (weaponDrawn) body.Add(BodyState.WeaponDrawn);
            // BodyState.Wet has no reliable source in this ClientStructs version (no IsWet on Character); never set.
        });

        Guard("activity", () =>
        {
            if (flags.Contains(ConditionFlag.Crafting) || flags.Contains(ConditionFlag.PreparingToCraft) || flags.Contains(ConditionFlag.ExecutingCraftingAction)) activities.Add(ActivityKind.Crafting);
            if (flags.Contains(ConditionFlag.Gathering) || flags.Contains(ConditionFlag.ExecutingGatheringAction)) activities.Add(ActivityKind.Gathering);
            if (flags.Contains(ConditionFlag.Fishing)) activities.Add(ActivityKind.Fishing);
            if (flags.Contains(ConditionFlag.Performing)) activities.Add(ActivityKind.Performing);
            if (flags.Contains(ConditionFlag.OccupiedInQuestEvent) || flags.Contains(ConditionFlag.OccupiedInEvent)) activities.Add(ActivityKind.NpcDialogue);
            if (flags.Contains(ConditionFlag.WatchingCutscene) || flags.Contains(ConditionFlag.WatchingCutscene78) || flags.Contains(ConditionFlag.OccupiedInCutSceneEvent)) activities.Add(ActivityKind.Cutscene);
            if (_clientState.IsGPosing) activities.Add(ActivityKind.GPose);
            if (flags.Contains(ConditionFlag.TradeOpen)) activities.Add(ActivityKind.Trading);
            if (flags.Contains(ConditionFlag.PlayingMiniGame) || flags.Contains(ConditionFlag.PlayingLordOfVerminion) || flags.Contains(ConditionFlag.ChocoboRacing)
             || flags.Contains(ConditionFlag.WaitingForTripleTriadMatch) || flags.Contains(ConditionFlag.WaitingForTripleTriadMatch83)) activities.Add(ActivityKind.MiniGame);
            if (flags.Contains(ConditionFlag.OccupiedSummoningBell)) activities.Add(ActivityKind.RetainerOrMarket);
            if (player.CurrentMinion is not null) activities.Add(ActivityKind.MinionSummoned);
        });

        Guard("emote", () =>
        {
            ref var ec = ref chara->EmoteController;
            emoteId      = ec.EmoteId;
            emoteLooping = ec.IsInEmoteLoop() || chara->Mode == CharacterModes.EmoteLoop;
            if (!emoteLooping && chara->Mode != CharacterModes.AnimLock && chara->Mode != CharacterModes.InPositionLoop && !ec.IsEmoting())
                emoteId = 0;
            pose = ec.GetPosture() switch
            {
                EmoteController.Posture.SittingInChair  => PoseKind.Sitting,
                EmoteController.Posture.SittingOnGround => PoseKind.GroundSitting,
                EmoteController.Posture.Dozing          => PoseKind.Dozing,
                EmoteController.Posture.Normal          => PoseKind.Standing,
                _                                       => null,
            };
        });

        var hp = player.MaxHp == 0 ? 100 : (int)Math.Clamp(player.CurrentHp * 100L / player.MaxHp, 0, 100);

        return new GameStateSnapshot
        {
            BuiltAtUtc          = nowUtc,
            Sequence            = Current.Sequence,
            LoggedIn            = true,
            PlayerName          = name,
            HomeWorldId         = homeWorld,
            CurrentWorldId      = curWorld,
            PlayerNameWithWorld = $"{name}@{_catalog.WorldName(homeWorld)}",
            RaceId              = race,
            ClanId              = clan,
            Gender              = gender,
            TerritoryId         = territoryId,
            MapId               = _clientState.MapId,
            TerritoryIntendedUse = territory?.IntendedUse ?? 0,
            RegionPlaceNameId   = territory?.RegionPlaceNameId ?? 0,
            ZonePlaceNameId     = territory?.ZonePlaceNameId ?? 0,
            PlaceNameId         = territory?.PlaceNameId ?? 0,
            IsSanctuary         = _slow.IsSanctuary,
            IsIndoors           = _slow.IsIndoors,
            IsInnRoom           = _catalog.IsInnRoom(territoryId),
            Housing             = _slow.Housing,
            Biome               = biome,
            InDuty              = inDuty,
            ContentFinderConditionId = cfcId,
            ContentKind         = contentKind,
            InFate              = _slow.InFate,
            WeatherId           = _slow.WeatherId,
            WeatherGroups       = _slow.WeatherGroups,
            EorzeaTime          = _slow.EorzeaTime,
            LocalTime           = nowUtc.ToLocalTime(),
            ServerTimeUtc       = nowUtc,
            ActiveEvents        = _slow.ActiveEvents,
            ClassJobId          = jobId,
            Role                = job?.Role,
            Discipline          = job?.Discipline,
            Level               = player.Level,
            GearsetId           = _slow.GearsetId,
            WeaponDrawn         = weaponDrawn,
            OnlineStatusId      = player.OnlineStatus.RowId,
            Statuses            = _slow.Statuses,
            HpPercent           = hp,
            Movement            = movement,
            Body                = body,
            Activities          = activities,
            EmoteId             = emoteId,
            EmoteLooping        = emoteLooping,
            Pose                = pose,
            PartySize           = _slow.PartySize,
            InAlliance          = _slow.InAlliance,
            CrossWorldParty     = flags.Contains(ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance),
            PartyMembers        = _slow.PartyMembers,
            RawConditionFlags   = raw,
        };
    }

    private unsafe SlowFields ReadSlowFields(IPlayerCharacter player, DateTime nowUtc)
    {
        var s = new SlowFields();

        Guard("weather", () =>
        {
            var wm = WeatherManager.Instance();
            if (wm is not null)
            {
                s.WeatherId     = wm->GetCurrentWeather();
                s.WeatherGroups = _catalog.WeatherGroups(s.WeatherId);
            }
        });

        Guard("eorzeaTime", () =>
        {
            var fw = CsFramework.Instance();
            if (fw is not null)
            {
                var et = fw->ClientTime.EorzeaTime;
                s.EorzeaTime = TimeSpan.FromSeconds(((et % 86400) + 86400) % 86400);
            }
        });

        Guard("statuses", () =>
        {
            var list = new List<StatusEntry>();
            foreach (var st in player.StatusList)
            {
                if (st.StatusId == 0)
                    continue;
                list.Add(new StatusEntry(st.StatusId, Math.Max(1, (int)st.Param), st.RemainingTime));
            }

            s.Statuses = list;
        });

        Guard("party", () =>
        {
            var count = _party.Length;
            s.PartySize  = Math.Max(1, count);
            s.InAlliance = _party.IsAlliance;
            var members = new List<string>(Math.Max(1, count));
            for (var i = 0; i < count; i++)
            {
                var m = _party[i];
                if (m is null)
                    continue;
                var world = m.World.ValueNullable?.Name.ExtractText() ?? _catalog.WorldName(m.World.RowId);
                members.Add($"{m.Name.TextValue}@{world}");
            }

            if (members.Count == 0)
                members.Add($"{player.Name.TextValue}@{_catalog.WorldName(player.HomeWorld.RowId)}");
            s.PartyMembers = members;
        });

        Guard("sanctuary", () =>
        {
            var ti = TerritoryInfo.Instance();
            s.IsSanctuary = ti is not null && ti->InSanctuary;
        });

        Guard("housing", () =>
        {
            var hm = HousingManager.Instance();
            var territoryId = (uint)_clientState.TerritoryType;
            var district    = _catalog.HousingDistrictOf(territoryId);
            var inside      = hm is not null && hm->IsInside();
            var intendedUse = _catalog.Territory(territoryId)?.IntendedUse ?? 0;
            s.IsIndoors = inside || intendedUse is StaticTables.IntendedUse.InnRoom or StaticTables.IntendedUse.HousingIndoor;

            if (district is null && !inside)
            {
                s.Housing = null;
                return;
            }

            if (hm is null)
            {
                s.Housing = new HousingInfo(district, -1, -1, inside, false, false, false, false);
                return;
            }

            var ward = hm->GetCurrentWard();
            var plot = hm->GetCurrentPlot();
            var isApartment = false;
            var isChambers  = false;
            var isOwn       = false;
            var isFc        = false;

            if (inside)
            {
                var house = hm->GetCurrentIndoorHouseId();
                isApartment = house.IsApartment;
                isChambers  = !isApartment && house.RoomNumber > 0;
                if (house.PlotIndex >= 0 && plot < 0)
                    plot = (sbyte)house.PlotIndex;
                if (ward < 0)
                    ward = (sbyte)house.WardIndex;

                var own = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate, 0);
                var fc  = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate, 0);
                isOwn = SameEstate(house, own);
                isFc  = SameEstate(house, fc);
                if (!isOwn && !isFc && !isApartment && hm->HasHousePermissions())
                    isOwn = true;
            }

            s.Housing = new HousingInfo(district, ward < 0 ? -1 : ward + 1, plot < 0 ? -1 : plot + 1, inside, isOwn, isFc, isApartment, isChambers);
        });

        Guard("fate", () =>
        {
            var fm = FateManager.Instance();
            s.InFate = fm is not null && fm->CurrentFate is not null;
        });

        Guard("gearset", () =>
        {
            var gm = RaptureGearsetModule.Instance();
            s.GearsetId = gm is null ? -1 : gm->CurrentGearsetIndex;
        });

        s.ActiveEvents = StaticTables.ActiveSeasonalEvents(DateOnly.FromDateTime(nowUtc.ToLocalTime()));
        return s;
    }

    private static bool SameEstate(HouseId a, HouseId b)
    {
        if (b.Id == 0 || b.TerritoryTypeId == 0)
            return false;
        if (a.Id == b.Id)
            return true;
        return a.TerritoryTypeId == b.TerritoryTypeId && a.WardIndex == b.WardIndex && a.PlotIndex == b.PlotIndex && a.IsApartment == b.IsApartment && a.RoomNumber == b.RoomNumber;
    }

    private void UpdateSpeed(Vector3 position, DateTime nowUtc)
    {
        if (_lastPosition is { } last)
        {
            var dt = (nowUtc - _lastPositionUtc).TotalSeconds;
            if (dt > 0.01)
            {
                var horizontal = new Vector2(position.X - last.X, position.Z - last.Z).Length();
                var instant    = (float)(horizontal / dt);
                // Light smoothing so a single frame hitch does not flip walking / running.
                _speed = instant > 50f ? _speed : _speed * 0.5f + instant * 0.5f;
            }
        }

        _lastPosition    = position;
        _lastPositionUtc = nowUtc;
    }

    private void Guard(string section, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex)
        {
            WarnOnce(section, ex, $"Lamina: StateTracker could not read '{section}'. The related conditions stay at their defaults.");
        }
    }

    private void WarnOnce(string key, Exception ex, string message)
    {
        if (_warned.Add(key))
            _log.Warning(ex, message);
    }
}
