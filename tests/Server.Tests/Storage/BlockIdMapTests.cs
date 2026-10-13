using Server.Storage;
using Shared.Blocks;

namespace Server.Tests.Storage;

public class BlockIdMapTests
{
    /// <summary>Реестр с блоками в заданном порядке (воздух добавляется сам, номером 0).</summary>
    internal static BlockRegistry Registry(params string[] names)
    {
        var registry = new BlockRegistry();
        foreach (var name in names)
            registry.Register(name, isOpaque: true);
        registry.Freeze();
        return registry;
    }

    private static BlockId Id(BlockRegistry registry, string name) => registry.Get(name).Id;

    private static BlockId[] ToRuntime(BlockIdMap map, params ushort[] saveIds)
    {
        var blocks = saveIds.Select(id => new BlockId(id)).ToArray();
        map.ToRuntime(blocks);
        return blocks;
    }

    private static BlockId[] ToSave(BlockIdMap map, params BlockId[] runtimeIds)
    {
        var blocks = new BlockId[runtimeIds.Length];
        map.ToSave(runtimeIds, blocks);
        return blocks;
    }

    [Fact]
    public void NewSave_GetsWholeRegistry_InRegistryOrder()
    {
        var registry = Registry("base:stone", "base:dirt");

        var map = new BlockIdMap([], registry);

        Assert.Equal(["base:air", "base:stone", "base:dirt"], map.NewNames);
        Assert.Empty(map.UnknownNames);
        Assert.True(map.IsIdentity);
        Assert.Equal([new BlockId(0), new BlockId(1), new BlockId(2)], ToRuntime(map, 0, 1, 2));
    }

    [Fact]
    public void SameRegistry_IsIdentity_NothingToAppend()
    {
        var map = new BlockIdMap(["base:air", "base:stone", "base:dirt"], Registry("base:stone", "base:dirt"));

        Assert.Empty(map.NewNames);
        Assert.True(map.IsIdentity);
    }

    [Fact]
    public void ReorderedRegistry_TranslatesByName_BothWays()
    {
        // В сохранении камень — 1, земля — 2; в новом реестре наоборот.
        var registry = Registry("base:dirt", "base:stone");

        var map = new BlockIdMap(["base:air", "base:stone", "base:dirt"], registry);

        Assert.False(map.IsIdentity);
        Assert.Empty(map.NewNames);
        var stone = Id(registry, "base:stone");
        var dirt = Id(registry, "base:dirt");
        Assert.Equal([BlockId.Air, stone, dirt], ToRuntime(map, 0, 1, 2));
        Assert.Equal([new BlockId(0), new BlockId(1), new BlockId(2)], ToSave(map, BlockId.Air, stone, dirt));
    }

    [Fact]
    public void UnknownSavedBlock_BecomesAir_NewBlockGetsNextNumber()
    {
        var registry = Registry("base:stone", "base:sand");

        var map = new BlockIdMap(["base:air", "old:gone", "base:stone"], registry);

        Assert.Equal(["old:gone"], map.UnknownNames);
        Assert.Equal(["base:sand"], map.NewNames);
        Assert.False(map.IsIdentity);
        Assert.Equal([BlockId.Air, Id(registry, "base:stone")], ToRuntime(map, 1, 2));
        // Номер 1 занят исчезнувшим блоком и не переиспользуется: песок получает 3.
        Assert.Equal([new BlockId(3)], ToSave(map, Id(registry, "base:sand")));
        Assert.Equal([Id(registry, "base:sand")], ToRuntime(map, 3));
    }

    [Fact]
    public void SaveIdOutsideTable_IsInvalidData()
    {
        var map = new BlockIdMap(["base:air", "base:stone"], Registry("base:stone"));

        Assert.Throws<InvalidDataException>(() => ToRuntime(map, 2));
    }

    [Theory]
    [InlineData("base:stone", "base:air")]  // номер 0 — не воздух
    [InlineData("base:air", "base:air")]    // повтор имени
    public void CorruptTable_IsInvalidData(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => new BlockIdMap([first, second], Registry("base:stone")));
    }
}
