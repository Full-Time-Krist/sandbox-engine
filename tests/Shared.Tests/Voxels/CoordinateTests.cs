using System.Numerics;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

public class CoordinateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 0)]
    [InlineData(32, 1)]
    [InlineData(63, 1)]
    [InlineData(64, 2)]
    [InlineData(-1, -1)]   // деление дало бы 0
    [InlineData(-31, -1)]  // деление дало бы 0
    [InlineData(-32, -1)]
    [InlineData(-33, -2)]  // деление дало бы −1
    [InlineData(-64, -2)]
    [InlineData(-65, -3)]
    public void Chunk_RoundsDown_ForAllAxes(int coordinate, int expectedChunk)
    {
        var pos = new BlockPos(coordinate, coordinate, coordinate);

        Assert.Equal(new ChunkPos(expectedChunk, expectedChunk, expectedChunk), pos.Chunk);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 31)]
    [InlineData(32, 0)]
    [InlineData(-1, 31)]   // остаток от деления дал бы −1
    [InlineData(-32, 0)]
    [InlineData(-33, 31)]
    public void Local_IsAlwaysInRange_ForAllAxes(int coordinate, int expectedLocal)
    {
        var pos = new BlockPos(coordinate, coordinate, coordinate);

        Assert.Equal(new LocalPos(expectedLocal, expectedLocal, expectedLocal), pos.Local);
    }

    [Fact]
    public void ChunkAndLocal_RoundTripToSameBlock()
    {
        for (var x = -70; x <= 70; x += 7)
        for (var y = -70; y <= 70; y += 5)
        for (var z = -70; z <= 70; z += 3)
        {
            var pos = new BlockPos(x, y, z);

            Assert.True(pos.Local.IsInBounds, $"{pos}: {pos.Local}");
            Assert.Equal(pos, pos.Chunk.ToWorld(pos.Local));
        }
    }

    [Fact]
    public void Axes_AreIndependent()
    {
        var pos = new BlockPos(-1, 32, 5);

        Assert.Equal(new ChunkPos(-1, 1, 0), pos.Chunk);
        Assert.Equal(new LocalPos(31, 0, 5), pos.Local);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 32)]
    [InlineData(-1, -32)]
    [InlineData(-2, -64)]
    public void ChunkOrigin_IsCornerWithSmallestCoordinates(int chunk, int expectedOrigin)
    {
        var origin = new ChunkPos(chunk, chunk, chunk).Origin;

        Assert.Equal(new BlockPos(expectedOrigin, expectedOrigin, expectedOrigin), origin);
        Assert.Equal(new LocalPos(0, 0, 0), origin.Local);
    }

    [Fact]
    public void Containing_FloorsPoint()
    {
        Assert.Equal(new BlockPos(0, 0, 0), BlockPos.Containing(new Vector3(0.5f, 0f, 0.999f)));
        Assert.Equal(new BlockPos(-1, -1, -1), BlockPos.Containing(new Vector3(-0.001f, -0.5f, -1f)));
        Assert.Equal(new BlockPos(2, -2, 10), BlockPos.Containing(new Vector3(2.0f, -1.5f, 10.25f)));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 0, 1)]
    [InlineData(0, 0, 1, 32)]
    [InlineData(0, 1, 0, 1024)]
    [InlineData(31, 31, 31, ChunkMath.Volume - 1)]
    public void LocalIndex_UsesXzyOrder(int x, int y, int z, int expectedIndex)
    {
        Assert.Equal(expectedIndex, new LocalPos(x, y, z).Index);
    }

    [Fact]
    public void LocalIndex_RoundTripsForWholeChunk()
    {
        for (var i = 0; i < ChunkMath.Volume; i++)
        {
            var local = LocalPos.FromIndex(i);

            Assert.True(local.IsInBounds);
            Assert.Equal(i, local.Index);
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(32, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 32)]
    public void IsInBounds_RejectsOutsideChunk(int x, int y, int z)
    {
        Assert.False(new LocalPos(x, y, z).IsInBounds);
    }
}
