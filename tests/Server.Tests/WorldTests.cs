using System.Diagnostics;
using Shared.Blocks;
using Shared.Content;
using Shared.Voxels;

namespace Server.Tests;

public sealed class WorldTests : IDisposable
{
    private readonly BlockRegistry _registry = GameContent.Load().Blocks;
    private readonly World _world;

    public WorldTests() => _world = NewWorld();

    public void Dispose() => _world.Dispose();

    private World NewWorld() => new(_registry, BaseBlocks.From(_registry), seed: 1, generatorThreads: 2);

    private void CollectUntilIdle() => CollectUntilIdle(_world);

    private static void CollectUntilIdle(World world)
    {
        var clock = Stopwatch.StartNew();
        while (world.GeneratingCount > 0 && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            world.CollectGenerated();
            Thread.Sleep(1);
        }
    }

    private static byte[] LightOf(World world, ChunkPos position)
    {
        var values = new byte[ChunkMath.Volume];
        world.Chunks.Get(position)!.Light.CopyTo(values);
        return values;
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
    public void Collect_OverBudget_TakesOneChunkPerTick_AndLosesNone()
    {
        // Свет считается при приходе чанка: за тик приходит столько, сколько укладывается в бюджет, но хотя бы один.
        using var world = new World(_registry, BaseBlocks.From(_registry), seed: 1, generatorThreads: 2) { CollectBudgetMs = 0 };
        for (var x = 0; x < 5; x++)
            world.TryRequest(new ChunkPos(x, 0, 0));
        // Пусть сгенерируются все: иначе за вызов и без бюджета мог бы прийти один чанк.
        Thread.Sleep(500);
        var clock = Stopwatch.StartNew();
        while (world.GeneratingCount > 0 && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var before = world.Chunks.Count;
            world.CollectGenerated();
            Assert.InRange(world.Chunks.Count - before, 0, 1);
            Thread.Sleep(1);
        }

        Assert.Equal(5, world.Chunks.Count);
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

        Assert.Equal([3, 4, 5], removed.Select(p => p.X).Order());
        Assert.Equal(3, _world.Chunks.Count);
        Assert.All(_world.Chunks.All, c => Assert.True(c.Position.X < 3));
    }

    [Fact]
    public void Light_AfterUnload_IsAsIfChunkWasNeverLoaded()
    {
        // Куб 3 × 3 × 3 чанков вокруг поверхности. Уходит чанк над поверхностью (воздух под солнцем), а в его
        // восточном соседе — каменная коробка, открытая только на запад: её свет приходит из уходящего чанка.
        var surface = BlockPos.Containing(_world.SpawnPosition).Chunk;
        var gone = surface with { Y = surface.Y + 1 };
        var region = new List<ChunkPos>();
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
            region.Add(new ChunkPos(surface.X + x, surface.Y + y, surface.Z + z));
        var rest = region.Where(p => p != gone).ToList();
        region.ForEach(p => _world.TryRequest(p));
        CollectUntilIdle();
        BuildBoxOpenToWest(_world, (gone with { X = gone.X + 1 }).Origin);
        var before = rest.ToDictionary(p => p, p => LightOf(_world, p));

        _world.Unload(p => p != gone);

        using var without = NewWorld();
        rest.ForEach(p => without.TryRequest(p));
        CollectUntilIdle(without);
        BuildBoxOpenToWest(without, (gone with { X = gone.X + 1 }).Origin);
        Assert.Contains(rest, p => !before[p].AsSpan().SequenceEqual(LightOf(_world, p)));
        Assert.All(rest, p => Assert.True(LightOf(without, p).AsSpan().SequenceEqual(LightOf(_world, p)), $"Свет чанка {p} после выгрузки соседа"));
    }

    /// <summary>Коробка у западной границы чанка: внутри воздух (x 0–5, y и z 10–15), стены из камня, запад открыт.</summary>
    private void BuildBoxOpenToWest(World world, BlockPos origin)
    {
        var stone = BaseBlocks.From(_registry).Stone;
        for (var x = 0; x <= 6; x++)
        for (var y = 9; y <= 16; y++)
        for (var z = 9; z <= 16; z++)
        {
            var wall = x == 6 || y is 9 or 16 || z is 9 or 16;
            Assert.True(world.TrySetBlock(origin + new BlockPos(x, y, z), wall ? stone : BlockId.Air));
        }
    }
}
