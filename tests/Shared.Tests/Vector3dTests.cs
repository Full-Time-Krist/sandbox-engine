using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Tests;

public class Vector3dTests
{
    [Fact]
    public void Arithmetic_AndDistance()
    {
        var a = new Vector3d(1, 2, 3);
        var b = new Vector3d(4, 6, 3);

        Assert.Equal(new Vector3d(5, 8, 6), a + b);
        Assert.Equal(new Vector3d(-3, -4, 0), a - b);
        Assert.Equal(new Vector3d(2, 4, 6), a * 2);
        Assert.Equal(5, Vector3d.Distance(a, b));
        Assert.Equal(new Vector3d(1.5, 2, 3), a + new Vector3(0.5f, 0, 0));
    }

    [Fact]
    public void Lerp_InterpolatesBetweenPoints()
    {
        var a = new Vector3d(1_000_000, 0, 0);
        var b = new Vector3d(1_000_001, 10, 0);

        Assert.Equal(new Vector3d(1_000_000.25, 2.5, 0), Vector3d.Lerp(a, b, 0.25));
    }

    [Fact]
    public void FarFromCenter_KeepsSubBlockPrecision()
    {
        // Во float 1 000 000,03 превращается в 1 000 000 (шаг 1/16), в double — нет.
        var position = new Vector3d(1_000_000, 0, 0) + new Vector3(0.03f, 0, 0);

        Assert.Equal(0.03, position.X - 1_000_000, precision: 6);
        Assert.Equal(0f, (float)position.X - 1_000_000f);
    }

    [Fact]
    public void ImplicitFromVector3_AndExplicitBack()
    {
        Vector3d v = new Vector3(1.5f, -2f, 3f);

        Assert.Equal(new Vector3d(1.5, -2, 3), v);
        Assert.Equal(new Vector3(1.5f, -2f, 3f), v.ToVector3());
    }

    [Fact]
    public void IsFinite_DetectsNaNAndInfinity()
    {
        Assert.True(new Vector3d(1, 2, 3).IsFinite);
        Assert.False(new Vector3d(double.NaN, 0, 0).IsFinite);
        Assert.False(new Vector3d(0, double.PositiveInfinity, 0).IsFinite);
    }

    [Fact]
    public void Raycast_FarFromCenter_HitsBlockThroughItsFace()
    {
        // Глаз в 0,03 блока перед гранью блока x = 1 000 001. Во float координата 1 000 000,97 округлилась бы
        // до 1 000 001,0 — глаз оказался бы внутри блока, и луч вернул бы «попадание изнутри» без нормали.
        const int Far = 1_000_000;
        var world = new ChunkMap();
        var block = new BlockPos(Far + 1, 5, 5);
        world.Set(new Chunk(block.Chunk));
        world.TrySetBlock(block, new BlockId(1));

        var hit = VoxelRaycast.Cast(world, new Vector3d(Far + 0.97, 5.5, 5.5), Vector3.UnitX, 6f);

        Assert.NotNull(hit);
        Assert.Equal(block, hit.Value.Block);
        Assert.Equal(new BlockPos(-1, 0, 0), hit.Value.Normal);
        Assert.Equal(0.03f, hit.Value.Distance, precision: 4);
    }

    [Fact]
    public void BlockPos_Containing_FarFromCenter()
    {
        Assert.Equal(new BlockPos(1_000_000, -1, 0), BlockPos.Containing(new Vector3d(1_000_000.97, -0.03, 0)));
    }
}
