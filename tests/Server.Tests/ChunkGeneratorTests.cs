using System.Diagnostics;
using Shared.Blocks;
using Shared.Voxels;

namespace Server.Tests;

public class ChunkGeneratorTests
{
    private readonly TerrainGenerator _terrain;

    public ChunkGeneratorTests()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();
        _terrain = new TerrainGenerator(blocks, seed: 3);
    }

    private static List<Chunk> TakeAll(ChunkGenerator generator, int expected)
    {
        var chunks = new List<Chunk>();
        var clock = Stopwatch.StartNew();
        while (chunks.Count < expected && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (generator.TryTakeCompleted(out var chunk))
                chunks.Add(chunk);
            else
                Thread.Sleep(1);
        }
        return chunks;
    }

    [Fact]
    public void EveryRequest_IsCompletedOnce_SameAsDirectGeneration()
    {
        using var generator = new ChunkGenerator(_terrain, workerCount: 4);
        var positions = new List<ChunkPos>();
        for (var x = -3; x < 3; x++)
        for (var y = -1; y < 3; y++)
        for (var z = -3; z < 3; z++)
            positions.Add(new ChunkPos(x, y, z));

        positions.ForEach(generator.Request);
        var chunks = TakeAll(generator, positions.Count);

        Assert.Equal(positions.Count, chunks.Count);
        Assert.Equal(positions.ToHashSet(), chunks.Select(c => c.Position).ToHashSet());
        Assert.Equal(0, generator.Pending);
        Assert.False(generator.TryTakeCompleted(out _));

        // Несколько потоков на одном генераторе дают те же блоки, что и генерация по одному.
        foreach (var chunk in chunks)
            Assert.True(chunk.Blocks.SequenceEqual(_terrain.Generate(chunk.Position).Blocks), $"Чанк {chunk.Position} отличается");
    }

    [Fact]
    public void Pending_CountsUntilTaken()
    {
        using var generator = new ChunkGenerator(_terrain, workerCount: 1);

        generator.Request(new ChunkPos(0, 0, 0));
        generator.Request(new ChunkPos(1, 0, 0));
        Assert.Equal(2, generator.Pending);

        TakeAll(generator, 1);
        Assert.Equal(1, generator.Pending);
    }

    [Fact]
    public void Dispose_DropsQueuedRequests_AndReturnsQuickly()
    {
        var generator = new ChunkGenerator(_terrain, workerCount: 1);
        // Тысячи чанков у поверхности — это секунды работы. Dispose не должен их дожидаться.
        for (var i = 0; i < 5000; i++)
            generator.Request(new ChunkPos(i, 0, 0));

        var clock = Stopwatch.StartNew();
        generator.Dispose();

        Assert.True(clock.ElapsedMilliseconds < 500, $"Dispose занял {clock.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void ExceptionInWorker_IsRethrownOnTake()
    {
        // Нужен любой сбой внутри Generate в фоновом потоке: генератор без набора блоков
        // падает с NullReferenceException на первом же блоке.
        using var generator = new ChunkGenerator(new TerrainGenerator(null!, seed: 1), workerCount: 1);
        generator.Request(new ChunkPos(0, 0, 0));

        var clock = Stopwatch.StartNew();
        InvalidOperationException? error = null;
        while (error == null && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { generator.TryTakeCompleted(out _); Thread.Sleep(1); }
            catch (InvalidOperationException e) { error = e; }
        }

        Assert.NotNull(error);
        Assert.IsType<NullReferenceException>(error.InnerException);
    }
}
