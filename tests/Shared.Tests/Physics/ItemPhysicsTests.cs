using System.Numerics;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Shared.Tests.Physics;

public class ItemPhysicsTests
{
    [Fact]
    public void FallsAndRestsOnFloor()
    {
        var (chunks, blocks, stone) = World();
        chunks.TrySetBlock(new BlockPos(5, 0, 5), stone);
        var state = new ItemBodyState(new(5.5, 4, 5.5));
        for (var i = 0; i < 100; i++) state = ItemPhysics.Step(state, chunks, blocks);
        Assert.Equal(1, state.Position.Y, 8);
        Assert.True(state.OnGround);
        Assert.Equal(0, state.Velocity.Y);
    }

    [Fact]
    public void FastItemCannotCrossWall()
    {
        var (chunks, blocks, stone) = World();
        chunks.TrySetBlock(new BlockPos(7, 5, 5), stone);
        var state = ItemPhysics.Step(new(new(5.5, 5.2, 5.5), new Vector3(60, 0, 0)), chunks, blocks);
        Assert.Equal(6.875, state.Position.X, 8);
        Assert.Equal(0, state.Velocity.X);
    }

    [Fact]
    public void UnloadedChunkStopsBody()
    {
        var (chunks, blocks, _) = World();
        var start = new Vector3d(31.7, 5, 5);
        var state = ItemPhysics.Step(new(start, new Vector3(20, 0, 0)), chunks, blocks);
        Assert.Equal(start, state.Position);
        Assert.Equal(Vector3.Zero, state.Velocity);
    }

    private static (ChunkMap, BlockRegistry, BlockId) World()
    {
        var chunks = new ChunkMap();
        chunks.Set(new Chunk(new ChunkPos(0, 0, 0)));
        var blocks = new BlockRegistry();
        var stone = blocks.Register("test:stone", true).Id;
        blocks.Freeze();
        return (chunks, blocks, stone);
    }
}
