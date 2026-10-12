using Shared.Blocks;
using Shared.Items;

namespace Shared.Tests.Items;

public class HotbarTests
{
    private static Hotbar CreateDefault()
    {
        var registry = new BlockRegistry();
        return Hotbar.CreateDefault(BaseBlocks.Register(registry));
    }

    [Fact]
    public void Default_HasSixBlocks_FirstSelected()
    {
        var hotbar = CreateDefault();

        Assert.Equal(0, hotbar.SelectedSlot);
        for (var i = 0; i < Hotbar.Size; i++)
            Assert.False(hotbar[i].IsAir);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Hotbar.Size)]
    [InlineData(int.MaxValue)]
    public void TrySelect_OutOfRange_KeepsSelection(int slot)
    {
        var hotbar = CreateDefault();
        hotbar.TrySelect(3);

        Assert.False(hotbar.TrySelect(slot));
        Assert.Equal(3, hotbar.SelectedSlot);
    }

    [Fact]
    public void TrySelect_ChangesSelectedItem()
    {
        var hotbar = CreateDefault();

        Assert.True(hotbar.TrySelect(4));
        Assert.Equal(hotbar[4], hotbar.SelectedItem);
    }

    [Fact]
    public void WrongNumberOfItems_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Hotbar(new BlockId[3]));
    }
}
