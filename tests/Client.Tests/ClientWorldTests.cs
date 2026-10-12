using Shared.Blocks;
using Shared.Voxels;

namespace Client.Tests;

public class ClientWorldTests
{
    private static List<ChunkPos> TakeDirty(ClientWorld world)
    {
        var dirty = new List<ChunkPos>();
        world.TakeDirty(dirty);
        return dirty;
    }

    [Fact]
    public void AddChunk_MarksItDirty()
    {
        var world = new ClientWorld();

        world.AddChunk(new Chunk(new ChunkPos(0, 0, 0)));

        Assert.Equal([new ChunkPos(0, 0, 0)], TakeDirty(world));
        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void AddChunk_MarksLoadedNeighborsDirty_ButNotOthers()
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(new ChunkPos(1, 0, 0)));  // сосед по +X
        world.AddChunk(new Chunk(new ChunkPos(5, 0, 0)));  // далеко
        world.AddChunk(new Chunk(new ChunkPos(1, 1, 0)));  // сосед по диагонали — не сосед по грани
        TakeDirty(world);

        world.AddChunk(new Chunk(new ChunkPos(0, 0, 0)));

        var dirty = TakeDirty(world);
        Assert.Equal(2, dirty.Count);
        Assert.Contains(new ChunkPos(0, 0, 0), dirty);
        Assert.Contains(new ChunkPos(1, 0, 0), dirty);
    }

    private static ClientWorld CreateWorldAround(ChunkPos center)
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(center));
        foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) })
            world.AddChunk(new Chunk(new ChunkPos(center.X + dx, center.Y + dy, center.Z + dz)));
        TakeDirty(world);
        return world;
    }

    [Fact]
    public void SetBlock_Inside_MarksOnlyItsChunk()
    {
        var world = CreateWorldAround(new ChunkPos(0, 0, 0));

        Assert.True(world.SetBlock(new BlockPos(10, 10, 10), new BlockId(1)));

        Assert.Equal([new ChunkPos(0, 0, 0)], TakeDirty(world));
    }

    [Theory]
    [InlineData(0, 10, 10, -1, 0, 0)]
    [InlineData(31, 10, 10, 1, 0, 0)]
    [InlineData(10, 0, 10, 0, -1, 0)]
    [InlineData(10, 31, 10, 0, 1, 0)]
    [InlineData(10, 10, 0, 0, 0, -1)]
    [InlineData(10, 10, 31, 0, 0, 1)]
    public void SetBlock_OnBorder_AlsoMarksNeighborBehindIt(int x, int y, int z, int nx, int ny, int nz)
    {
        var world = CreateWorldAround(new ChunkPos(0, 0, 0));

        world.SetBlock(new BlockPos(x, y, z), new BlockId(1));

        var dirty = TakeDirty(world);
        Assert.Equal(2, dirty.Count);
        Assert.Contains(new ChunkPos(0, 0, 0), dirty);
        Assert.Contains(new ChunkPos(nx, ny, nz), dirty);
    }

    [Fact]
    public void SetBlock_InCorner_MarksThreeNeighbors()
    {
        var world = CreateWorldAround(new ChunkPos(0, 0, 0));

        world.SetBlock(new BlockPos(0, 0, 0), new BlockId(1));

        Assert.Equal(4, TakeDirty(world).Count);
    }

    [Fact]
    public void SetBlock_InMissingChunk_Fails()
    {
        var world = new ClientWorld();

        Assert.False(world.SetBlock(new BlockPos(0, 0, 0), new BlockId(1)));
        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void GetNeighbors_ReturnsChunksOnEachSide()
    {
        var world = new ClientWorld();
        var center = new ChunkPos(-1, 0, 2);
        world.AddChunk(new Chunk(center));
        world.AddChunk(new Chunk(new ChunkPos(-2, 0, 2)));
        world.AddChunk(new Chunk(new ChunkPos(-1, 0, 3)));

        var neighbors = world.GetNeighbors(center);

        Assert.Equal(new ChunkPos(-2, 0, 2), neighbors.NegX?.Position);
        Assert.Equal(new ChunkPos(-1, 0, 3), neighbors.PosZ?.Position);
        Assert.Null(neighbors.PosX);
        Assert.Null(neighbors.NegY);
        Assert.Null(neighbors.PosY);
        Assert.Null(neighbors.NegZ);
    }
}
