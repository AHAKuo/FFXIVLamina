namespace Lamina.Runtime.State;

/// <summary>
/// Decides whether two snapshots differ in a way conditions can observe. Ignores BuiltAtUtc / Sequence,
/// sub-minute clock changes and anything else that would make every tick a "change".
/// </summary>
public static class SnapshotComparer
{
    /// <summary> True when no condition-visible field differs. </summary>
    public static bool Equivalent(GameStateSnapshot a, GameStateSnapshot b)
    {
        if (ReferenceEquals(a, b))
            return true;

        return a.LoggedIn == b.LoggedIn
         && a.PlayerName == b.PlayerName
         && a.HomeWorldId == b.HomeWorldId
         && a.CurrentWorldId == b.CurrentWorldId
         && a.RaceId == b.RaceId
         && a.ClanId == b.ClanId
         && a.Gender == b.Gender
         && a.TerritoryId == b.TerritoryId
         && a.MapId == b.MapId
         && a.TerritoryIntendedUse == b.TerritoryIntendedUse
         && a.RegionPlaceNameId == b.RegionPlaceNameId
         && a.ZonePlaceNameId == b.ZonePlaceNameId
         && a.PlaceNameId == b.PlaceNameId
         && a.IsSanctuary == b.IsSanctuary
         && a.IsIndoors == b.IsIndoors
         && a.IsInnRoom == b.IsInnRoom
         && Equals(a.Housing, b.Housing)
         && a.Biome == b.Biome
         && a.InDuty == b.InDuty
         && a.ContentFinderConditionId == b.ContentFinderConditionId
         && a.ContentKind == b.ContentKind
         && a.InFate == b.InFate
         && a.WeatherId == b.WeatherId
         && a.WeatherGroups.SetEquals(b.WeatherGroups)
         && MinuteOf(a.EorzeaTime) == MinuteOf(b.EorzeaTime)
         && MinuteOf(a.LocalTime) == MinuteOf(b.LocalTime)
         && MinuteOf(a.ServerTimeUtc) == MinuteOf(b.ServerTimeUtc)
         && a.ActiveEvents.SequenceEqual(b.ActiveEvents)
         && a.ClassJobId == b.ClassJobId
         && a.Role == b.Role
         && a.Discipline == b.Discipline
         && a.Level == b.Level
         && a.GearsetId == b.GearsetId
         && a.WeaponDrawn == b.WeaponDrawn
         && a.OnlineStatusId == b.OnlineStatusId
         && StatusesEquivalent(a.Statuses, b.Statuses)
         && a.HpPercent == b.HpPercent
         && a.Movement.SetEquals(b.Movement)
         && a.Body.SetEquals(b.Body)
         && a.Activities.SetEquals(b.Activities)
         && a.EmoteId == b.EmoteId
         && a.EmoteLooping == b.EmoteLooping
         && a.Pose == b.Pose
         && a.PartySize == b.PartySize
         && a.InAlliance == b.InAlliance
         && a.CrossWorldParty == b.CrossWorldParty
         && a.PartyMembers.SequenceEqual(b.PartyMembers, StringComparer.Ordinal)
         && a.RawConditionFlags.SetEquals(b.RawConditionFlags);
    }

    private static long MinuteOf(TimeSpan t)
        => (long)t.TotalMinutes;

    private static long MinuteOf(DateTime t)
        => t.Ticks / TimeSpan.TicksPerMinute;

    /// <summary> Status ids and stack counts matter; remaining time does not (it changes every frame). </summary>
    private static bool StatusesEquivalent(IReadOnlyList<StatusEntry> a, IReadOnlyList<StatusEntry> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].StatusId != b[i].StatusId || a[i].Stacks != b[i].Stacks)
                return false;
        return true;
    }
}
