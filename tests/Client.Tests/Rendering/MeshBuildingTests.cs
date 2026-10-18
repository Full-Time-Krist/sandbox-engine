using System.Diagnostics;
using Client.Rendering;
using Shared.Blocks;
using Shared.Content;
using Shared.Voxels;

namespace Client.Tests.Rendering;

/// <summary>Построение мешей в фоне: снимок чанка, очередь потоков, версии готовых мешей.</summary>
public class MeshBuildingTests
{
    private readonly BaseBlocks _blocks;
    private readonly ChunkMesher _mesher;

    public MeshBuildingTests()
    {
        var content = GameContent.Load();
        var registry = content.Blocks;
        _blocks = BaseBlocks.From(registry);
        _mesher = new ChunkMesher(new BlockVisuals(registry, BlockVisuals.AppearancesFrom(content.BlockTextures)));
    }

    /// <summary>Чанк, у которого блок зависит от координат: так видно, что каждый блок попал на своё место.</summary>
    private Chunk Patterned(ChunkPos position, int salt)
    {
        var ids = new[] { BlockId.Air, _blocks.Stone, _blocks.Dirt, _blocks.Grass, _blocks.Sand };
        var blocks = new BlockId[ChunkMath.Volume];
        for (var i = 0; i < blocks.Length; i++)
        {
            var p = LocalPos.FromIndex(i);
            blocks[i] = ids[(p.X * 7 + p.Y * 3 + p.Z * 5 + salt) % ids.Length];
        }
        return Chunk.FromBlocks(position, blocks);
    }

    // --- Снимок ---

    [Fact]
    public void Snapshot_Interior_EqualsChunk()
    {
        var chunk = Patterned(new ChunkPos(1, 2, 3), salt: 0);
        var snapshot = new ChunkSnapshot();

        snapshot.Capture(chunk, default);

        Assert.Equal(chunk.Position, snapshot.Position);
        for (var i = 0; i < ChunkMath.Volume; i++)
        {
            var p = LocalPos.FromIndex(i);
            Assert.Equal(chunk.Get(p), snapshot[p.X, p.Y, p.Z]);
        }
    }

    [Fact]
    public void Snapshot_Border_HoldsAdjacentLayerOfEachNeighbor()
    {
        var center = Patterned(default, salt: 0);
        var neighbors = new ChunkNeighbors(
            Patterned(new ChunkPos(-1, 0, 0), 1), Patterned(new ChunkPos(1, 0, 0), 2),
            Patterned(new ChunkPos(0, -1, 0), 3), Patterned(new ChunkPos(0, 1, 0), 4),
            Patterned(new ChunkPos(0, 0, -1), 5), Patterned(new ChunkPos(0, 0, 1), 6));
        var snapshot = new ChunkSnapshot();

        snapshot.Capture(center, neighbors);

        const int L = ChunkMath.Size - 1, S = ChunkMath.Size;
        for (var a = 0; a < S; a++)
        for (var b = 0; b < S; b++)
        {
            Assert.Equal(neighbors.NegX!.Get(new LocalPos(L, a, b)), snapshot[-1, a, b]);
            Assert.Equal(neighbors.PosX!.Get(new LocalPos(0, a, b)), snapshot[S, a, b]);
            Assert.Equal(neighbors.NegY!.Get(new LocalPos(a, L, b)), snapshot[a, -1, b]);
            Assert.Equal(neighbors.PosY!.Get(new LocalPos(a, 0, b)), snapshot[a, S, b]);
            Assert.Equal(neighbors.NegZ!.Get(new LocalPos(a, b, L)), snapshot[a, b, -1]);
            Assert.Equal(neighbors.PosZ!.Get(new LocalPos(a, b, 0)), snapshot[a, b, S]);
        }
    }

    [Fact]
    public void Snapshot_Reused_ForgetsPreviousChunk()
    {
        // Снимки берутся из пула: прошлое содержимое (и соседи) не должно просочиться в новый снимок.
        var snapshot = new ChunkSnapshot();
        var full = Patterned(default, salt: 1);
        snapshot.Capture(full, new ChunkNeighbors(full, full, full, full, full, full));

        var single = new Chunk(new ChunkPos(5, 0, 0));
        single.Set(new LocalPos(0, 0, 0), _blocks.Stone);
        snapshot.Capture(single, default);

        Assert.Equal(1, snapshot.Blocks.ToArray().Count(b => !b.IsAir));
        Assert.Equal(new ChunkPos(5, 0, 0), snapshot.Position);
    }

    // --- Очередь построения ---

    [Fact]
    public void Queue_BuildsEverySubmittedChunk_SameAsDirectBuild()
    {
        using var queue = new MeshBuildQueue(_mesher, workerCount: 3);
        var chunks = Enumerable.Range(0, 20).Select(i => Patterned(new ChunkPos(i, 0, 0), salt: i)).ToList();

        for (var i = 0; i < chunks.Count; i++)
        {
            var snapshot = queue.RentSnapshot();
            snapshot.Capture(chunks[i], default);
            queue.Submit(snapshot, version: 100 + i);
        }

        var results = new List<MeshBuildResult>();
        var clock = Stopwatch.StartNew();
        while (results.Count < chunks.Count && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (queue.TryTakeCompleted(out var result))
                results.Add(result);
            else
                Thread.Sleep(1);
        }

        Assert.Equal(chunks.Count, results.Count);
        var expected = new ChunkMeshData();
        foreach (var result in results)
        {
            var index = result.Position.X;
            Assert.Equal(100 + index, result.Version);
            _mesher.Build(chunks[index], default, expected);
            Assert.Equal(expected.Vertices, result.Data.Vertices);
            Assert.Equal(expected.Indices, result.Data.Indices);
        }
    }

    [Fact]
    public void Queue_ExceptionInWorker_IsRethrownOnTake()
    {
        // Мешер без описаний блоков падает с NullReferenceException на первом же непустом блоке.
        using var queue = new MeshBuildQueue(new ChunkMesher(null!), workerCount: 1);
        var snapshot = queue.RentSnapshot();
        snapshot.Capture(Patterned(default, salt: 1), default);
        queue.Submit(snapshot, 1);

        var clock = Stopwatch.StartNew();
        InvalidOperationException? error = null;
        while (error == null && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { queue.TryTakeCompleted(out _); Thread.Sleep(1); }
            catch (InvalidOperationException e) { error = e; }
        }

        Assert.IsType<NullReferenceException>(error?.InnerException);
    }

    // --- Версии ---

    [Fact]
    public void Versions_AcceptOnlyLatestSubmission()
    {
        var versions = new MeshVersions();
        var position = new ChunkPos(1, 0, 0);

        var first = versions.Next(position);
        var second = versions.Next(position); // блок изменился, пока строился первый меш

        Assert.False(versions.TryAccept(position, first));
        Assert.True(versions.TryAccept(position, second));
        Assert.Equal(0, versions.Count);
    }

    [Fact]
    public void Versions_ResultsArrivingOutOfOrder_OlderIsRejectedAfterNewer()
    {
        // Потоков несколько: вторая отправка может достроиться раньше первой.
        var versions = new MeshVersions();
        var position = new ChunkPos(1, 0, 0);
        var first = versions.Next(position);
        var second = versions.Next(position);

        Assert.True(versions.TryAccept(position, second));
        Assert.False(versions.TryAccept(position, first));
    }

    [Fact]
    public void Versions_ForgottenChunk_RejectsItsBuild()
    {
        var versions = new MeshVersions();
        var position = new ChunkPos(1, 0, 0);
        var version = versions.Next(position);

        versions.Forget(position); // чанк выгружен, пока строился

        Assert.False(versions.TryAccept(position, version));
    }

    [Fact]
    public void Versions_AreIndependentPerChunk()
    {
        var versions = new MeshVersions();
        var a = versions.Next(new ChunkPos(0, 0, 0));
        var b = versions.Next(new ChunkPos(1, 0, 0));

        Assert.True(versions.TryAccept(new ChunkPos(0, 0, 0), a));
        Assert.True(versions.TryAccept(new ChunkPos(1, 0, 0), b));
    }
}
