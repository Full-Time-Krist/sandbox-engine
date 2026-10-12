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
