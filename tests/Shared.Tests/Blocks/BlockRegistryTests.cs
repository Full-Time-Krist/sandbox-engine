using System.Runtime.CompilerServices;
using Shared.Blocks;

namespace Shared.Tests.Blocks;

public class BlockRegistryTests
{
    [Fact]
    public void Air_IsRegisteredFirst_WithIdZero()
    {
        var registry = new BlockRegistry();

        var air = registry.Get(BlockRegistry.AirName);
        Assert.Equal(BlockId.Air, air.Id);
        Assert.False(air.IsOpaque);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void IsSolid_AirNo_DefaultYes_ExplicitNo_UnknownYes()
    {
        var registry = new BlockRegistry();
        var stone = registry.Register("base:stone", isOpaque: true).Id;
        var plant = registry.Register("base:plant", isOpaque: false, isSolid: false).Id;

        Assert.False(registry.IsSolid(BlockId.Air));
        Assert.True(registry.IsSolid(stone));
        Assert.False(registry.IsSolid(plant));
        // Id не из реестра — только из испорченных данных: лучше упереться, чем провалиться.
        Assert.True(registry.IsSolid(new BlockId(1000)));
    }

    [Fact]
    public void Register_AssignsSequentialIds_AndLooksUpBothWays()
    {
        var registry = new BlockRegistry();

        var stone = registry.Register("base:stone", isOpaque: true);
        var dirt = registry.Register("base:dirt", isOpaque: true);

        Assert.Equal(new BlockId(1), stone.Id);
        Assert.Equal(new BlockId(2), dirt.Id);
        Assert.Same(stone, registry.Get(stone.Id));
        Assert.Same(dirt, registry.Get("base:dirt"));
        Assert.True(registry.TryGet("base:stone", out var found));
        Assert.Same(stone, found);
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new BlockRegistry();
        registry.Register("base:stone", isOpaque: true);

        Assert.Throws<InvalidOperationException>(() => registry.Register("base:stone", isOpaque: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("stone")]           // нет namespace
    [InlineData("base:")]
    [InlineData(":stone")]
    [InlineData("Base:Stone")]      // заглавные буквы
    [InlineData("base:stone block")]
    [InlineData("base:stone:extra")]
    public void Register_InvalidName_Throws(string name)
    {
        var registry = new BlockRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(name, isOpaque: true));
    }

    [Fact]
    public void Register_AfterFreeze_Throws()
    {
        var registry = new BlockRegistry();
        registry.Freeze();

        Assert.Throws<InvalidOperationException>(() => registry.Register("base:stone", isOpaque: true));
    }

    [Fact]
    public void Get_Unknown_Throws()
    {
        var registry = new BlockRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.Get(new BlockId(42)));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("base:unknown"));
        Assert.False(registry.TryGet("base:unknown", out _));
    }

    [Fact]
    public void BlockId_TakesTwoBytes()
    {
        // Массив блоков чанка хранит BlockId напрямую: лишнее поле удвоило бы память мира.
        Assert.Equal(2, Unsafe.SizeOf<BlockId>());
    }
}
