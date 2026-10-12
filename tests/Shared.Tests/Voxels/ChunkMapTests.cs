using Shared.Blocks;
using Shared.Protocol;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

public class ChunkMapTests
{
    private static readonly BlockId Stone = new(1);

    [Fact]
    public void GetBlock_InMissingChunk_IsAir()
    {
        var map = new ChunkMap();

        Assert.True(map.GetBlock(new BlockPos(5, 5, 5)).IsAir);
    }

    [Fact]
    public void TrySetBlock_InMissingChunk_Fails()
    {
        var map = new ChunkMap();

        Assert.False(map.TrySetBlock(new BlockPos(5, 5, 5), Stone));
        Assert.Equal(0, map.Count);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, -1)]   // угол чанка (−1, 0, −1)
    [InlineData(-33, 40, 70)]
    public void SetBlock_ThenGet_UsesRightChunk(int x, int y, int z)
    {
        var pos = new BlockPos(x, y, z);
        var map = new ChunkMap();
        map.Set(new Chunk(pos.Chunk));

        Assert.True(map.TrySetBlock(pos, Stone));

        Assert.Equal(Stone, map.GetBlock(pos));
        Assert.Equal(Stone, map.Get(pos.Chunk)!.Get(pos.Local));
        Assert.True(map.GetBlock(pos + new BlockPos(0, 1, 0)).IsAir);
    }

    [Fact]
    public void FromBlocks_CopiesBlocksAndCountsNonAir()
    {
        var source = new Chunk(new ChunkPos(1, 2, 3));
        source.Set(new LocalPos(0, 0, 0), Stone);
        source.Set(new LocalPos(31, 31, 31), Stone);

        var copy = Chunk.FromBlocks(source.Position, source.Blocks);
        source.Set(new LocalPos(0, 0, 0), BlockId.Air); // копия не должна зависеть от источника

        Assert.Equal(2, copy.NonAirCount);
        Assert.Equal(Stone, copy.Get(new LocalPos(0, 0, 0)));
        Assert.Equal(new ChunkPos(1, 2, 3), copy.Position);
    }

    [Fact]
    public void FromBlocks_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Chunk.FromBlocks(default, new BlockId[10]));
    }

    [Fact]
    public void ChunkData_RoundTripsChunk()
    {
        var chunk = new Chunk(new ChunkPos(-2, 0, 5));
        chunk.Set(new LocalPos(3, 4, 5), Stone);

        var restored = ChunkData.From(chunk).ToChunk();

        Assert.Equal(chunk.Position, restored.Position);
        Assert.True(chunk.Blocks.SequenceEqual(restored.Blocks));
        Assert.Equal(1, restored.NonAirCount);
    }

    [Fact]
    public void ChunkData_EmptyChunk_SendsNoBlocks()
    {
        var data = ChunkData.From(new Chunk(new ChunkPos(0, 2, 0)));

        Assert.True(data.IsEmpty);
        Assert.True(data.ToChunk().IsEmpty);
    }
}
