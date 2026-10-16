using Shared.Blocks;
using Shared.Items;

namespace Shared.Tests.Items;

public class ItemRegistryTests
{
    private readonly BlockRegistry _blocks = new();
    private readonly BaseBlocks _base;
    private readonly ItemRegistry _items = new();

    public ItemRegistryTests()
    {
        _base = BaseBlocks.Register(_blocks);
        _blocks.Freeze();
    }

    [Fact]
    public void AirIsZero_AndIsEmptySlot()
    {
        var air = _items.Get(ItemId.Air);

        Assert.Equal(ItemRegistry.AirName, air.Name);
        Assert.Null(air.Block);
        Assert.Equal(ItemId.Air, default(ItemStack).Item);
    }

    [Fact]
    public void EveryBlockExceptAir_GetsItemWithSameName_ThatPlacesIt()
    {
        _items.RegisterBlockItems(_blocks);

        Assert.Equal(_blocks.Count, _items.Count); // воздух-предмет уже был, воздух-блок второго не дал
        foreach (var block in _blocks.All.Where(b => !b.Id.IsAir))
        {
            var item = _items.Get(block.Name);
            Assert.Equal(block.Id, item.Block);
            Assert.Equal(ItemRegistry.BlockStackSize, item.MaxStackSize);
            Assert.Equal(item.Id, _items.ForBlock(block.Id));
        }
    }

    [Fact]
    public void ForBlock_WithoutItem_IsAir()
    {
        Assert.Equal(ItemId.Air, _items.ForBlock(_base.Stone));
        Assert.Equal(ItemId.Air, _items.ForBlock(BlockId.Air));
    }

    [Fact]
    public void ItemThatIsNotBlock_PlacesNothing_AndIsNotDropOfAnyBlock()
    {
        _items.RegisterBlockItems(_blocks);
        var stick = _items.Register("base:stick", 64);
        // Второй предмет, ставящий камень, не перебивает добычу с камня.
        _items.Register("base:stone_brush", 1, _base.Stone);

        Assert.Null(stick.Block);
        Assert.Equal(_items.Get("base:stone").Id, _items.ForBlock(_base.Stone));
    }

    [Theory]
    [InlineData("stone")]
    [InlineData("base:Stone")]
    [InlineData("base:stone:x")]
    public void InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => _items.Register(name, 64));
    }

    [Fact]
    public void Duplicate_Frozen_ZeroStack_AirBlock_Throw()
    {
        _items.Register("base:stick", 64);

        Assert.Throws<InvalidOperationException>(() => _items.Register("base:stick", 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => _items.Register("base:empty", 0));
        Assert.Throws<ArgumentException>(() => _items.Register("base:void", 64, BlockId.Air));
        _items.Freeze();
        Assert.Throws<InvalidOperationException>(() => _items.Register("base:rock", 64));
    }

    [Fact]
    public void UnknownId_Throws()
    {
        Assert.False(_items.Contains(new ItemId(100)));
        Assert.Throws<KeyNotFoundException>(() => _items.Get(new ItemId(100)));
    }

    [Fact]
    public void Stack_EmptyIsSingleValue()
    {
        var stone = new ItemId(1);

        Assert.Equal(ItemStack.Empty, new ItemStack(stone, 0));
        Assert.Equal(ItemStack.Empty, new ItemStack(ItemId.Air, 5));
        Assert.True(new ItemStack(stone, 0).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemStack(stone, -1));
    }
}
