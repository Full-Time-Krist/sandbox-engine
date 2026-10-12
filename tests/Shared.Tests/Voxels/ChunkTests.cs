using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

public class ChunkTests
{
    private static readonly BlockId Stone = new(1);
    private static readonly BlockId Dirt = new(2);

    [Fact]
    public void NewChunk_IsAllAir()
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));

        Assert.True(chunk.IsEmpty);
        Assert.Equal(ChunkMath.Volume, chunk.Blocks.Length);
        foreach (var block in chunk.Blocks)
            Assert.True(block.IsAir);
    }

    [Fact]
    public void Set_ThenGet_ReturnsBlock_AtCorners()
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));
        var first = new LocalPos(0, 0, 0);
        var last = new LocalPos(31, 31, 31);

        chunk.Set(first, Stone);
        chunk.Set(last, Dirt);

        Assert.Equal(Stone, chunk.Get(first));
        Assert.Equal(Dirt, chunk.Get(last));
        Assert.True(chunk.Get(new LocalPos(1, 0, 0)).IsAir);
    }

    [Fact]
    public void Blocks_FollowsLocalIndexOrder()
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));
        var pos = new LocalPos(3, 7, 11);

        chunk.Set(pos, Stone);

        Assert.Equal(Stone, chunk.Blocks[pos.Index]);
    }

    [Theory]
    [InlineData(32, 0, 0)]  // без проверки это тихо стало бы блоком (0, 0, 1)
    [InlineData(-1, 0, 0)]
    [InlineData(0, 32, 0)]
    [InlineData(0, 0, 32)]
    public void OutOfBounds_Throws(int x, int y, int z)
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));
        var pos = new LocalPos(x, y, z);

        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Get(pos));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Set(pos, Stone));
    }

    [Fact]
    public void NonAirCount_TracksChanges()
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));
        var a = new LocalPos(1, 2, 3);
        var b = new LocalPos(4, 5, 6);

        chunk.Set(a, Stone);
        chunk.Set(b, Stone);
        Assert.Equal(2, chunk.NonAirCount);

        chunk.Set(a, Dirt);         // замена одного блока другим
        Assert.Equal(2, chunk.NonAirCount);

        chunk.Set(a, Dirt);         // повторная запись того же
        Assert.Equal(2, chunk.NonAirCount);

        chunk.Set(a, BlockId.Air);
        chunk.Set(a, BlockId.Air);  // воздух поверх воздуха
        Assert.Equal(1, chunk.NonAirCount);

        chunk.Set(b, BlockId.Air);
        Assert.True(chunk.IsEmpty);
    }
}
