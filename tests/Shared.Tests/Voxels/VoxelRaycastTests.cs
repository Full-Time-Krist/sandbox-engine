using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

public class VoxelRaycastTests
{
    private static readonly BlockId Stone = new(1);

    /// <summary>Реестр без блоков: id 1 в нём не зарегистрирован, и форма у него — куб.</summary>
    private static readonly BlockRegistry Blocks = new();

    /// <summary>Мир из чанков вокруг начала координат (от −1 до 0 по каждой оси) с заданными блоками.</summary>
    private static ChunkMap WorldWith(params BlockPos[] blocks)
    {
        var map = new ChunkMap();
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            map.Set(new Chunk(new ChunkPos(x, y, z)));

        foreach (var block in blocks)
            Assert.True(map.TrySetBlock(block, Stone));
        return map;
    }

    [Fact]
    public void StraightDown_HitsTopFace()
    {
        var world = WorldWith(new BlockPos(0, 5, 0));

        var hit = VoxelRaycast.Cast(world, Blocks, new Vector3(0.5f, 10.5f, 0.5f), -Vector3.UnitY, 10f);

        Assert.NotNull(hit);
        Assert.Equal(new BlockPos(0, 5, 0), hit.Value.Block);
        Assert.Equal(new BlockPos(0, 1, 0), hit.Value.Normal);
        Assert.Equal(4.5f, hit.Value.Distance, precision: 4); // от y = 10,5 до верха блока y = 6
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void EachAxis_NormalPointsBackAtViewer(int dx, int dy, int dz)
    {
        var direction = new Vector3(dx, dy, dz);
        var origin = new Vector3(5.5f, 5.5f, 5.5f);
        var target = new BlockPos(5 + dx * 3, 5 + dy * 3, 5 + dz * 3);
        var world = WorldWith(target);

        var hit = VoxelRaycast.Cast(world, Blocks, origin, direction, 10f);

        Assert.NotNull(hit);
        Assert.Equal(target, hit.Value.Block);
        Assert.Equal(new BlockPos(-dx, -dy, -dz), hit.Value.Normal);
        Assert.Equal(2.5f, hit.Value.Distance, precision: 4);
    }

    [Fact]
    public void CrossesChunkBorder_IntoNegativeCoordinates()
    {
        var world = WorldWith(new BlockPos(-1, 0, 0));

        var hit = VoxelRaycast.Cast(world, Blocks, new Vector3(0.5f, 0.5f, 0.5f), -Vector3.UnitX, 5f);

        Assert.NotNull(hit);
        Assert.Equal(new BlockPos(-1, 0, 0), hit.Value.Block);
        Assert.Equal(new BlockPos(1, 0, 0), hit.Value.Normal);
        Assert.Equal(0.5f, hit.Value.Distance, precision: 4);
    }

    [Fact]
    public void BeyondReach_Misses()
    {
        var world = WorldWith(new BlockPos(10, 0, 0));

        Assert.Null(VoxelRaycast.Cast(world, Blocks, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 6f));
    }

    [Fact]
    public void UnloadedChunks_CountAsAir()
    {
        var world = new ChunkMap();

        Assert.Null(VoxelRaycast.Cast(world, Blocks, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 50f));
    }

    [Fact]
    public void StartInsideBlock_HitsIt_WithZeroNormal()
    {
        var world = WorldWith(new BlockPos(2, 2, 2));

        var hit = VoxelRaycast.Cast(world, Blocks, new Vector3(2.5f, 2.5f, 2.5f), Vector3.UnitX, 6f);

        Assert.Equal(new BlockHit(new BlockPos(2, 2, 2), default, 0f), hit);
    }

    [Fact]
    public void ZeroDirection_Misses()
    {
        var world = WorldWith(new BlockPos(1, 0, 0));

        Assert.Null(VoxelRaycast.Cast(world, Blocks, new Vector3(0.5f, 0.5f, 0.5f), Vector3.Zero, 6f));
    }

    [Fact]
    public void OriginOnBoundary_MovingNegative_EntersNeighborImmediately()
    {
        // Точка x = 5,0 лежит в блоке 5; при движении к −X луч сразу входит в блок 4.
        var world = WorldWith(new BlockPos(4, 0, 0));

        var hit = VoxelRaycast.Cast(world, Blocks, new Vector3(5f, 0.5f, 0.5f), -Vector3.UnitX, 6f);

        Assert.NotNull(hit);
        Assert.Equal(new BlockPos(4, 0, 0), hit.Value.Block);
        Assert.Equal(0f, hit.Value.Distance, precision: 4);
    }

    [Fact]
    public void MatchesBruteForce_OnRandomRays()
    {
        // Примитивный метод: идти по лучу мелкими шагами. Медленно, но очевидно правильно (кроме касаний рёбер,
        // которые мелкий шаг может «перепрыгнуть», поэтому сравниваем с допуском по расстоянию).
        var random = new Random(12345);
        var blocks = Enumerable.Range(0, 120)
            .Select(_ => new BlockPos(random.Next(-12, 12), random.Next(-12, 12), random.Next(-12, 12)))
            .Distinct()
            .ToArray();
        var world = WorldWith(blocks);

        for (var i = 0; i < 100; i++)
        {
            var origin = new Vector3(random.NextSingle() * 4 - 2, random.NextSingle() * 4 - 2, random.NextSingle() * 4 - 2);
            var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            if (!world.GetBlock(BlockPos.Containing(origin)).IsAir)
                continue;

            var hit = VoxelRaycast.Cast(world, Blocks, origin, direction, 15f);
            var expected = BruteForce(world, origin, direction, 15f);

            if (expected is null)
            {
                Assert.True(hit is null || hit.Value.Distance > 14.9f, $"Луч {i}: DDA нашёл {hit}, перебор — ничего");
                continue;
            }

            Assert.NotNull(hit);
            Assert.True(MathF.Abs(hit.Value.Distance - expected.Value.Distance) < 0.01f,
                $"Луч {i}: DDA {hit.Value.Block} на {hit.Value.Distance}, перебор {expected.Value.Block} на {expected.Value.Distance}");
        }
    }

    private static (BlockPos Block, float Distance)? BruteForce(ChunkMap world, Vector3 origin, Vector3 direction, float maxDistance)
    {
        const float Step = 0.001f;
        for (var t = 0f; t <= maxDistance; t += Step)
        {
            var block = BlockPos.Containing(origin + direction * t);
            if (!world.GetBlock(block).IsAir)
                return (block, t);
        }

        return null;
    }
}
