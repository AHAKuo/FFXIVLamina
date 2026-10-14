using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.State;
using Xunit;

namespace Lamina.Tests.State;

public class SnapshotComparerTests
{
    private static GameStateSnapshot Base() => new()
    {
        LoggedIn      = true,
        Sequence      = 1,
        TerritoryId   = 128,
        WeatherId     = 1,
        EorzeaTime    = new TimeSpan(10, 15, 20),
        LocalTime     = new DateTime(2026, 10, 15, 14, 5, 10),
        ServerTimeUtc = new DateTime(2026, 10, 15, 11, 5, 10, DateTimeKind.Utc),
        Statuses      = new[] { new StatusEntry(50, 1, 10f) },
        Movement      = new HashSet<MovementState> { MovementState.Idle },
        PartyMembers  = new[] { "A@B" },
    };

    [Fact]
    public void IgnoresSequenceAndBuiltAt()
    {
        var a = Base();
        var b = Base() with { Sequence = 99, BuiltAtUtc = DateTime.UtcNow.AddHours(1) };
        Assert.True(SnapshotComparer.Equivalent(a, b));
    }

    [Fact]
    public void IgnoresSubMinuteClockChanges_ButNotMinuteChanges()
    {
        var a = Base();
        Assert.True(SnapshotComparer.Equivalent(a, a with { EorzeaTime = new TimeSpan(10, 15, 59) }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { EorzeaTime = new TimeSpan(10, 16, 0) }));
        Assert.True(SnapshotComparer.Equivalent(a, a with { LocalTime = a.LocalTime.AddSeconds(30) }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { LocalTime = a.LocalTime.AddMinutes(1) }));
    }

    [Fact]
    public void IgnoresStatusRemainingTime_ButNotStacksOrIds()
    {
        var a = Base();
        Assert.True(SnapshotComparer.Equivalent(a, a with { Statuses = new[] { new StatusEntry(50, 1, 2f) } }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { Statuses = new[] { new StatusEntry(50, 2, 2f) } }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { Statuses = Array.Empty<StatusEntry>() }));
    }

    [Fact]
    public void DetectsRelevantChanges()
    {
        var a = Base();
        Assert.False(SnapshotComparer.Equivalent(a, a with { TerritoryId = 129 }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { WeatherId = 2 }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { Movement = new HashSet<MovementState> { MovementState.Mounted } }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { Housing = new HousingInfo(HousingDistrict.Mist, 1, 1, true, true, false, false, false) }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { PartyMembers = new[] { "A@B", "C@D" } }));
        Assert.False(SnapshotComparer.Equivalent(a, a with { LoggedIn = false }));
    }

    [Fact]
    public void HousingInfo_RecordEquality()
    {
        var a = Base() with { Housing = new HousingInfo(HousingDistrict.Mist, 1, 1, true, true, false, false, false) };
        var b = a with { Housing = new HousingInfo(HousingDistrict.Mist, 1, 1, true, true, false, false, false) };
        Assert.True(SnapshotComparer.Equivalent(a, b));
    }
}
