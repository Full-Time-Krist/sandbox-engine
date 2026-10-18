using System.Diagnostics;
using Shared.Blocks;
using Shared.Content;
using Shared.Voxels;

namespace Server.Tests;

public class ChunkGeneratorTests
{
    private readonly TerrainGenerator _terrain;

    public ChunkGeneratorTests()
    {
        var registry = GameContent.Load().Blocks;
        var blocks = BaseBlocks.From(registry);
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
        using var generator = new ChunkGenerator(_terrain.Generate, workerCount: 4);
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
        using var generator = new ChunkGenerator(_terrain.Generate, workerCount: 1);

        generator.Request(new ChunkPos(0, 0, 0));
        generator.Request(new ChunkPos(1, 0, 0));
        Assert.Equal(2, generator.Pending);

        TakeAll(generator, 1);
        Assert.Equal(1, generator.Pending);
    }

    /// <summary>
    /// Dispose не дорабатывает очередь: доделывается только чанк, который уже генерируется. Проверяется без часов
    /// (раньше — «Dispose быстрее 500 мс», и тест падал, когда первый чанк с JIT-компиляцией шума шёл под нагрузкой):
    /// первый чанк держим, пока Dispose не очистит очередь и не встанет ждать поток, и считаем, сколько чанков сделано.
    /// </summary>
    [Fact]
    public void Dispose_DropsQueuedRequests_FinishesOnlyChunkInProgress()
    {
        var produced = 0;
        using var firstStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var generator = new ChunkGenerator(position =>
        {
            if (Interlocked.Increment(ref produced) == 1)
            {
                firstStarted.Set();
                release.Wait(TestContext.Current.CancellationToken);
            }
            return _terrain.Generate(position);
        }, workerCount: 1);
        for (var i = 0; i < 5000; i++)
            generator.Request(new ChunkPos(i, 0, 0));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "Генерация первого чанка не началась");

        var disposer = new Thread(generator.Dispose);
        disposer.Start();
        // Dispose дошёл до ожидания потока (Join), значит, очередь уже очищена: теперь отпускаем первый чанк.
        var clock = Stopwatch.StartNew();
        while ((disposer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && clock.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(1);
        release.Set();

        Assert.True(disposer.Join(TimeSpan.FromSeconds(10)), "Dispose не завершился");
        Assert.Equal(1, Volatile.Read(ref produced));
    }

    [Fact]
    public void ExceptionInWorker_IsRethrownOnTake()
    {
        // Нужен любой сбой внутри Generate в фоновом потоке: генератор без набора блоков
        // падает с NullReferenceException на первом же блоке.
        using var generator = new ChunkGenerator(new TerrainGenerator(null!, seed: 1).Generate, workerCount: 1);
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
