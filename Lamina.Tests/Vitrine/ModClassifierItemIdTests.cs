using Penumbra.Api.Enums;
using Vitrine;
using Vitrine.Catalog;
using Xunit;

namespace Lamina.Tests.Vitrine;

public class ModClassifierItemIdTests
{
    private sealed class RowLike
    {
        public uint RowId { get; init; }
    }

    public readonly record struct CustomId(ulong Id);

    private sealed class WrappedId
    {
        public CustomId ItemId { get; init; }
    }

    [Fact]
    public void TupleWithUint_IsRead()
        => Assert.Equal(1234ul, ModClassifier.ExtractItemId((ChangedItemType.Item, 1234u)));

    [Fact]
    public void TupleWithUlong_IsRead()
        => Assert.Equal(99ul, ModClassifier.ExtractItemId((ChangedItemType.ItemOffhand, 99ul)));

    [Fact]
    public void RowIdProperty_IsRead()
        => Assert.Equal(42ul, ModClassifier.ExtractItemId(new RowLike { RowId = 42 }));

    [Fact]
    public void WrappedItemId_IsUnwrapped()
        => Assert.Equal(7ul, ModClassifier.ExtractItemId(new WrappedId { ItemId = new CustomId(7) }));

    [Fact]
    public void NullOrUnknown_IsNull()
    {
        Assert.Null(ModClassifier.ExtractItemId(null));
        Assert.Null(ModClassifier.ExtractItemId("Company Tabard"));
    }

    [Theory]
    [InlineData(VitrineCategory.Gear, true)]
    [InlineData(VitrineCategory.Weapon, true)]
    [InlineData(VitrineCategory.Accessory, true)]
    [InlineData(VitrineCategory.Hair, false)]
    [InlineData(VitrineCategory.Emote, false)]
    public void IsEquippable_OnlyForWornCategories(VitrineCategory category, bool expected)
        => Assert.Equal(expected, ModClassifier.IsEquippable(category));
}
