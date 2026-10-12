using System.Diagnostics;
using Shared.Blocks;
using Shared.Voxels;

namespace Server.Tests;

public sealed class WorldTests : IDisposable
{
    private readonly World _world;

    public WorldTests()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();
        _world = new World(blocks, seed: 1, generatorThreads: 2);
    }

    public void Dispose() => _world.Dispose();

    private void CollectUntilIdle()
    {
        var clock = Stopwatch.StartNew();
        while (_world.GeneratingCount > 0 && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            _world.CollectGenerated();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void NewWorld_IsEmpty_UntilRequested()
    {
        Assert.Equal(0, _world.Chunks.Count);
        Assert.Equal(0, _world.GeneratingCount);
    }

    [Fact]
    public void Request_GeneratesChunk_Once()
    {
        var position = new ChunkPos(2, 0, -1);

        Assert.True(_world.TryRequest(position));
        Assert.True(_world.TryRequest(position));
        Assert.Equal(1, _world.GeneratingCount);

        CollectUntilIdle();

        Assert.True(_world.Chunks.Contains(position));
        Assert.Equal(1, _world.Chunks.Count);
        Assert.True(_world.TryRequest(position)); // уже загружен — снова не генерируется
        Assert.Equal(0, _world.GeneratingCount);
    }

    [Fact]
    public void Requests_AreCappedAtMaxGenerating()
    {
        var accepted = Enumerable.Range(0, World.MaxGenerating + 10).Count(i => _world.TryRequest(new ChunkPos(i, 0, 0)));

        Assert.Equal(World.MaxGenerating, accepted);
        Assert.Equal(World.MaxGenerating, _world.GeneratingCount);
    }

    [Fact]
    public void Unload_RemovesOnlyUnneededChunks()
    {
        for (var x = 0; x < 6; x++)
            _world.TryRequest(new ChunkPos(x, 0, 0));
        CollectUntilIdle();

        var removed = _world.Unload(p => p.X < 3);

        Assert.Equal(3, removed);
        Assert.Equal(3, _world.Chunks.Count);
        Assert.All(_world.Chunks.All, c => Assert.True(c.Position.X < 3));
    }
}
