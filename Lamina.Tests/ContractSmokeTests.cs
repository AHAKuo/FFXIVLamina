using Lamina.Models;
using Lamina.Runtime;
using Xunit;

namespace Lamina.Tests;

public class ContractSmokeTests
{
    [Fact]
    public void TimeCondition_WrapsMidnight()
    {
        Assert.True(TimeCondition.InRange(23 * 60, 18 * 60, 6 * 60));
        Assert.True(TimeCondition.InRange(2 * 60, 18 * 60, 6 * 60));
        Assert.False(TimeCondition.InRange(12 * 60, 18 * 60, 6 * 60));
    }

    [Fact]
    public void DesignDraft_MergeOnTop_LaterWins()
    {
        var a = new DesignDraft();
        a.Items[EquipSlot.Head] = new Field<EquipItem>(new EquipItem(1), Provenance.None);
        var b = new DesignDraft();
        b.Items[EquipSlot.Head] = new Field<EquipItem>(new EquipItem(2), Provenance.None);
        a.MergeOnTop(b);
        Assert.Equal(2ul, a.Items[EquipSlot.Head].Value.ItemId);
        var frozen = a.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        Assert.NotEmpty(frozen.ContentHash);
    }
}
