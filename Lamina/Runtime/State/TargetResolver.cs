using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lamina.Models;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace Lamina.Runtime.State;

/// <summary> A target found in the object table right now. Indices are reused by the game, so never keep one across frames without <see cref="TargetResolver.Locate"/>. </summary>
public readonly record struct ResolvedActor(int Index, nint Address);

/// <summary> A character near you that a profile could target, for the picker. </summary>
/// <param name="Target"> The target identity to store in the profile. </param>
/// <param name="Name"> The character's name. </param>
/// <param name="Detail"> World name for players, the NPC kind for non-players. </param>
/// <param name="Distance"> Distance from you in yalms. </param>
public sealed record TargetCandidate(Target Target, string Name, string Detail, float Distance);

/// <summary>
/// Finds the game object a <see cref="Target"/> names and decides which objects are targetable at all. Only characters that
/// can take on the look of a character qualify: player characters, and non-player characters drawn with a human model (event
/// NPCs, retainers, humanoid battle NPCs). Minions, mounts, monsters, housing furniture and every other object kind are out.
/// Framework thread only (the object table refuses other threads); off-thread callers see "absent".
/// </summary>
public sealed unsafe class TargetResolver
{
    /// <summary> Object table indices at and above this hold group pose copies and UI / examine actors, never the world. </summary>
    public const int WorldObjectLimit = 200;

    /// <summary> How long an unresolved target waits before the table is scanned for it again. </summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(250);

    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly Func<uint, string> _worldName;
    private readonly Dictionary<Target, (ResolvedActor? Actor, DateTime ScannedUtc)> _cache = new();

    /// <param name="objects"> The object table. </param>
    /// <param name="framework"> Framework, for the thread check. </param>
    /// <param name="worldName"> World id to name, for the picker labels. </param>
    public TargetResolver(IObjectTable objects, IFramework framework, Func<uint, string> worldName)
    {
        _objects   = objects;
        _framework = framework;
        _worldName = worldName;
    }

    /// <summary>
    /// The object the target names right now, or null while it is not rendered. A cached hit is validated against the table
    /// (same character at the same index and address) on every call; a miss rescans the world part of the table at most every 250 ms.
    /// </summary>
    public ResolvedActor? Locate(Target target, DateTime nowUtc)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;

        if (_cache.TryGetValue(target, out var cached))
        {
            if (cached.Actor is { } actor)
            {
                var obj = ObjectAt(actor.Index);
                if (obj is not null && obj.Address == actor.Address && Matches(target, obj))
                    return actor;
            }
            else if (nowUtc - cached.ScannedUtc < RescanInterval)
            {
                return null;
            }
        }

        var found = Scan(target);
        _cache[target] = (found, nowUtc);
        return found;
    }

    /// <summary> Drops the cache for a target (profile edits, zone change). </summary>
    public void Forget(Target target)
        => _cache.Remove(target);

    /// <summary> Drops every cached location. </summary>
    public void Clear()
        => _cache.Clear();

    /// <summary> True when a named player target is the character you are logged in as (name and home world match). </summary>
    public bool IsLocalIdentity(Target target)
    {
        if (target.Kind != TargetKind.OtherPlayer || !_framework.IsInFrameworkUpdateThread)
            return false;

        return _objects.LocalPlayer is { } local && MatchesPlayer(target, local);
    }

    /// <summary> Every targetable character rendered near you (the local player excluded), nearest first. </summary>
    public IReadOnlyList<TargetCandidate> Candidates()
    {
        var result = new List<TargetCandidate>();
        if (!_framework.IsInFrameworkUpdateThread)
            return result;

        var local  = _objects.LocalPlayer;
        var origin = local?.Position ?? Vector3.Zero;
        var seen   = new HashSet<Target>();
        foreach (var obj in _objects)
        {
            if (obj.ObjectIndex >= WorldObjectLimit || (local is not null && obj.Address == local.Address) || !IsEligible(obj))
                continue;

            var target = Identify(obj);
            if (target is null || !seen.Add(target))
                continue;

            var detail = target.Kind == TargetKind.OtherPlayer ? _worldName(target.WorldId ?? 0) : NpcKind(obj);
            result.Add(new TargetCandidate(target, target.Name ?? string.Empty, detail, Vector3.Distance(origin, obj.Position)));
        }

        result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return result;
    }

    /// <summary>
    /// Whether an object can be targeted: a player character, or a non-player character (event NPC, retainer, battle NPC)
    /// whose draw object is a human model. Everything else (minions, mounts, monsters, furniture, aetherytes, ...) is not.
    /// </summary>
    public static bool IsEligible(IGameObject obj)
    {
        if (obj.Address == nint.Zero || obj.Name.TextValue.Length == 0)
            return false;

        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc:
                return obj is IPlayerCharacter;
            case ObjectKind.EventNpc:
            case ObjectKind.BattleNpc:
            case ObjectKind.Retainer:
                return obj is ICharacter && IsHuman(obj.Address);
            default:
                return false;
        }
    }

    /// <summary> The target identity of an eligible object, or null when it is not eligible. </summary>
    public static Target? Identify(IGameObject obj)
    {
        if (!IsEligible(obj))
            return null;

        return obj is IPlayerCharacter player
            ? Target.Player(player.Name.TextValue, player.HomeWorld.RowId)
            : Target.Npc(obj.Name.TextValue, obj.DataId == 0 ? null : obj.DataId);
    }

    /// <summary> Whether the object is the character the target names. </summary>
    public static bool Matches(Target target, IGameObject? obj)
    {
        if (obj is null || obj.Address == nint.Zero)
            return false;

        return target.Kind switch
        {
            TargetKind.OtherPlayer => obj is IPlayerCharacter player && MatchesPlayer(target, player),
            TargetKind.Character   => obj.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc or ObjectKind.Retainer
                                   && string.Equals(obj.Name.TextValue, target.Name, StringComparison.OrdinalIgnoreCase)
                                   && (target.DataId is null || target.DataId == obj.DataId)
                                   && IsEligible(obj),
            _                      => false,
        };
    }

    private static bool MatchesPlayer(Target target, IPlayerCharacter player)
        => string.Equals(player.Name.TextValue, target.Name, StringComparison.OrdinalIgnoreCase)
        && (target.WorldId is null || target.WorldId == player.HomeWorld.RowId);

    private ResolvedActor? Scan(Target target)
    {
        var local = _objects.LocalPlayer;
        foreach (var obj in _objects)
        {
            if (obj.ObjectIndex >= WorldObjectLimit)
                break;
            if (local is not null && obj.Address == local.Address)
                continue;
            if (Matches(target, obj))
                return new ResolvedActor(obj.ObjectIndex, obj.Address);
        }

        return null;
    }

    private IGameObject? ObjectAt(int index)
        => index >= 0 && index < _objects.Length ? _objects[index] : null;

    private static string NpcKind(IGameObject obj)
        => obj.ObjectKind switch
        {
            ObjectKind.Retainer  => "Retainer",
            ObjectKind.BattleNpc => "NPC (battle)",
            _                    => "NPC",
        };

    /// <summary> True when the object's draw object is a human character model (the kind Glamourer and Customize+ dress). </summary>
    private static bool IsHuman(nint address)
    {
        try
        {
            var draw = ((CsGameObject*)address)->DrawObject;
            return draw != null && draw->GetObjectType() == SceneObjectType.CharacterBase
                && ((CharacterBase*)draw)->GetModelType() == CharacterBase.ModelType.Human;
        }
        catch
        {
            return false;
        }
    }
}
