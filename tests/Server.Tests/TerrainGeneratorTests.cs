using Shared.Blocks;
using Shared.Voxels;

namespace Server.Tests;

public class TerrainGeneratorTests
{
    private readonly BaseBlocks _blocks;
    private readonly TerrainGenerator _generator;

    public TerrainGeneratorTests()
    {
        var registry = new BlockRegistry();
        _blocks = BaseBlocks.Register(registry);
        registry.Freeze();
        _generator = new TerrainGenerator(_blocks);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var position = new ChunkPos(-1, 1, 2);

        var a = _generator.Generate(position);
        var b = _generator.Generate(position);

        Assert.True(a.Blocks.SequenceEqual(b.Blocks));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-37, 85)]
    [InlineData(120, -128)]
    public void Column_HasSurfaceBlockOnTop_AirAbove_StoneDeep(int x, int z)
    {
        var map = new ChunkMap();
        for (var cy = 0; cy <= 2; cy++)
            map.Set(_generator.Generate(new BlockPos(x, cy * ChunkMath.Size, z).Chunk));

        var height = TerrainGenerator.SurfaceHeight(x, z);
        var surface = map.GetBlock(new BlockPos(x, height - 1, z));

        Assert.True(surface == _blocks.Grass || surface == _blocks.Sand, $"Сверху {surface}");
        Assert.True(map.GetBlock(new BlockPos(x, height, z)).IsAir);
        Assert.Equal(_blocks.Stone, map.GetBlock(new BlockPos(x, height - 10, z)));
    }

    [Fact]
    public void SurfaceHeight_FitsInsideWorldHeight()
    {
        var top = (World.MaxChunk.Y + 1) * ChunkMath.Size;

        for (var x = -128; x < 128; x += 3)
        for (var z = -128; z < 128; z += 3)
            Assert.InRange(TerrainGenerator.SurfaceHeight(x, z), 1, top - 1);
    }
}
