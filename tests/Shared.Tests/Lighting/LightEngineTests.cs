using Shared.Blocks;
using Shared.Lighting;
using Shared.Voxels;

namespace Shared.Tests.Lighting;

/// <summary>
/// Свет: солнце, тень под навесом, тёмные полости, свечение блоков, правки и порядок прихода чанков.
/// Главная проверка — сверка с расчётом «с нуля» (<see cref="Reference"/>, своя простая заливка от всех источников):
/// после любых правок свет должен совпадать с ним в каждом блоке.
/// </summary>
public class LightEngineTests
{
    private readonly BlockRegistry _blocks = new();
    private readonly BlockId _stone, _glass, _torch, _lamp, _glowshroom;

    public LightEngineTests()
    {
        _stone = _blocks.Register("test:stone", isOpaque: true).Id;
        _glass = _blocks.Register("test:glass", isOpaque: false).Id;
        _torch = _blocks.Register("test:torch", isOpaque: false, isSolid: false, light: 14).Id;
        _lamp = _blocks.Register("test:lamp", isOpaque: true, light: 10).Id;
        _glowshroom = _blocks.Register("test:glowshroom", isOpaque: false, isSolid: false, light: 5).Id;
        _blocks.Freeze();
    }

    /// <summary>Мир из чанков от −1 до 0 по всем осям (блоки от −32 до 31), по умолчанию — всё небо открыто.</summary>
    private (ChunkMap Chunks, LightEngine Engine) Create(ISkyAbove? sky = null, Action<ChunkMap>? build = null)
    {
        var chunks = new ChunkMap();
        foreach (var position in Region())
            chunks.Set(new Chunk(position));
        build?.Invoke(chunks);

        // Свет считается по мере прихода чанков, как на сервере.
        var loaded = new ChunkMap();
        var growing = new LightEngine(_blocks, loaded, sky ?? OpenSky.Instance);
        foreach (var chunk in chunks.All.ToList())
        {
            loaded.Set(chunk);
            growing.ChunkAdded(chunk);
        }
        return (loaded, growing);
    }

    private static IEnumerable<ChunkPos> Region()
    {
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            yield return new ChunkPos(x, y, z);
    }

    private void Fill(ChunkMap chunks, int x0, int y0, int z0, int x1, int y1, int z1, BlockId block)
    {
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
            Assert.True(chunks.TrySetBlock(new BlockPos(x, y, z), block));
    }

    private void Set(ChunkMap chunks, LightEngine engine, BlockPos pos, BlockId block)
    {
        Assert.True(chunks.TrySetBlock(pos, block));
        engine.BlockChanged(pos);
    }

    [Fact]
    public void OpenField_FullSunAbove_DarkInsideStone()
    {
        var (chunks, engine) = Create(build: c => Fill(c, -32, -32, -32, 31, 0, 31, _stone));

        Assert.Equal(15, engine.GetSky(new BlockPos(5, 1, 5)));
        Assert.Equal(15, engine.GetSky(new BlockPos(-20, 31, 7)));
        Assert.Equal(0, engine.GetSky(new BlockPos(5, 0, 5)));
        Assert.Equal(0, engine.GetSky(new BlockPos(5, -20, 5)));
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Fact]
    public void UnderRoof_LightComesFromEdges_OneLessPerBlock()
    {
        // Навес 3 × 3 на высоте 20 над полом: центр под ним в двух шагах от солнца, края — в одном.
        var (chunks, engine) = Create(build: c =>
        {
            Fill(c, -32, -32, -32, 31, 0, 31, _stone);
            Fill(c, 10, 20, 10, 12, 20, 12, _stone);
        });

        for (var y = 1; y <= 19; y++)
        {
            Assert.Equal(13, engine.GetSky(new BlockPos(11, y, 11)));
            Assert.Equal(14, engine.GetSky(new BlockPos(10, y, 11)));
            Assert.Equal(14, engine.GetSky(new BlockPos(12, y, 12)));
            Assert.Equal(15, engine.GetSky(new BlockPos(9, y, 11)));
        }
        Assert.Equal(15, engine.GetSky(new BlockPos(11, 21, 11)));
    }

    [Fact]
    public void ClosedCave_IsDark_AndOpeningLetsSunIn()
    {
        // Полость 5 × 5 × 5 в сплошном камне, через границу чанков (x от −2 до 2).
        var (chunks, engine) = Create(build: c =>
        {
            Fill(c, -32, -32, -32, 31, 31, 31, _stone);
            Fill(c, -2, -10, -2, 2, -6, 2, BlockId.Air);
        });
        Assert.Equal(0, engine.GetSky(new BlockPos(0, -8, 0)));

        // Шахта от верха мира до потолка полости: солнце идёт по ней вниз без потерь и растекается по полости.
        for (var y = 31; y >= -5; y--)
            Set(chunks, engine, new BlockPos(0, y, 0), BlockId.Air);
        Assert.Equal(15, engine.GetSky(new BlockPos(0, -10, 0)));
        Assert.Equal(14, engine.GetSky(new BlockPos(1, -10, 0)));
        Assert.Equal(11, engine.GetSky(new BlockPos(2, -10, 2))); // четыре шага от столба
        AssertMatchesReference(chunks, OpenSky.Instance);

        // Закрыли шахту — снова темно везде.
        Set(chunks, engine, new BlockPos(0, 0, 0), _stone);
        Assert.Equal(0, engine.GetSky(new BlockPos(0, -10, 0)));
        Assert.Equal(0, engine.GetSky(new BlockPos(0, -1, 0)));
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Fact]
    public void Torch_LightsDiamond_StoppedByWall()
    {
        // Два замкнутых зала в камне, между ними стена в один блок; факел у стены в первом зале.
        var (chunks, engine) = Create(build: c =>
        {
            Fill(c, -32, -32, -32, 31, 31, 31, _stone);
            Fill(c, -20, -20, -20, 20, 0, -1, BlockId.Air);
            Fill(c, -20, -20, 1, 20, 0, 20, BlockId.Air);
        });
        var torch = new BlockPos(0, -10, -1);
        Set(chunks, engine, torch, _torch);

        Assert.Equal(14, engine.GetBlock(torch));
        for (var d = 1; d <= 14; d++)
        {
            Assert.Equal(14 - d, engine.GetBlock(torch + new BlockPos(d, 0, 0)));
            Assert.Equal(Math.Max(0, 14 - d - 1), engine.GetBlock(torch + new BlockPos(-d, 0, -1)));
        }
        Assert.Equal(0, engine.GetBlock(new BlockPos(0, -10, 0)));  // стена
        Assert.Equal(0, engine.GetBlock(new BlockPos(0, -10, 1)));  // зал за стеной
        Assert.Equal(0, engine.GetSky(torch));
        AssertMatchesReference(chunks, OpenSky.Instance);

        // Убрали факел — темно.
        Set(chunks, engine, torch, BlockId.Air);
        Assert.Equal(0, engine.GetBlock(torch));
        Assert.Equal(0, engine.GetBlock(torch + new BlockPos(3, 0, 0)));
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Fact]
    public void WeakSource_InStrongLight_KeepsOwnLight_WhenStrongRemoved()
    {
        // Гриб (свет 5) рядом с факелом светит 13 — от факела. Убрали факел: у гриба снова его собственные 5.
        var (chunks, engine) = Create(build: c =>
        {
            Fill(c, -32, -32, -32, 31, 31, 31, _stone);
            Fill(c, -10, -10, -10, 10, 0, 10, BlockId.Air);
        });
        var torch = new BlockPos(0, -5, 0);
        var shroom = new BlockPos(1, -5, 0);
        Set(chunks, engine, shroom, _glowshroom);
        Set(chunks, engine, torch, _torch);
        Assert.Equal(13, engine.GetBlock(shroom));

        Set(chunks, engine, torch, BlockId.Air);
        Assert.Equal(5, engine.GetBlock(shroom));
        Assert.Equal(4, engine.GetBlock(torch));
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Fact]
    public void OpaqueGlowingBlock_LightsNeighbors_ButNotThroughOtherStone()
    {
        var (chunks, engine) = Create(build: c =>
        {
            Fill(c, -32, -32, -32, 31, 31, 31, _stone);
            Fill(c, -5, -5, -5, 5, 5, 5, BlockId.Air);
        });

        var lamp = new BlockPos(0, 0, 0);
        Set(chunks, engine, lamp, _lamp);
        Assert.Equal(10, engine.GetBlock(lamp));
        Assert.Equal(9, engine.GetBlock(new BlockPos(1, 0, 0)));
        Assert.Equal(5, engine.GetBlock(new BlockPos(5, 0, 0)));
        Assert.Equal(0, engine.GetBlock(new BlockPos(6, 0, 0)));  // камень за залом
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Fact]
    public void SkyAboveUnloaded_FollowsNaturalHeight()
    {
        // Загружен только слой чанков y = 0 (блоки 0–31); над ним «гора» при x < 0 (верх на 100) и «равнина» при x ≥ 0.
        var sky = new HeightSky((x, _) => x < 0 ? 100 : 10);
        var chunks = new ChunkMap();
        var engine = new LightEngine(_blocks, chunks, sky);
        foreach (var position in Region().Where(p => p.Y == 0))
        {
            var chunk = new Chunk(position);
            chunks.Set(chunk);
            engine.ChunkAdded(chunk);
        }

        Assert.Equal(15, engine.GetSky(new BlockPos(20, 0, 5)));
        Assert.Equal(0, engine.GetSky(new BlockPos(-20, 31, 5)));
        // У подножия горы солнце заходит сбоку, с минус одним на блок.
        Assert.Equal(14, engine.GetSky(new BlockPos(-1, 20, 5)));
        Assert.Equal(10, engine.GetSky(new BlockPos(-5, 20, 5)));
        AssertMatchesReference(chunks, sky);
    }

    [Fact]
    public void ChunkAbove_ArrivingWithRoof_ShadesChunkBelow()
    {
        // Снизу пусто, по рельефу над ним открыто; потом приходит чанк сверху с крышей во всю ширину.
        var chunks = new ChunkMap();
        var engine = new LightEngine(_blocks, chunks, OpenSky.Instance);
        var below = new Chunk(new ChunkPos(0, 0, 0));
        chunks.Set(below);
        engine.ChunkAdded(below);
        Assert.Equal(15, engine.GetSky(new BlockPos(5, 0, 5)));

        var above = new Chunk(new ChunkPos(0, 1, 0));
        for (var x = 0; x < 32; x++)
        for (var z = 0; z < 32; z++)
            above.Set(new LocalPos(x, 10, z), _stone);
        chunks.Set(above);
        engine.ChunkAdded(above);

        Assert.Equal(0, engine.GetSky(new BlockPos(5, 0, 5)));
        Assert.Equal(0, engine.GetSky(new BlockPos(5, 41, 5)));
        Assert.Equal(15, engine.GetSky(new BlockPos(5, 43, 5)));
        AssertMatchesReference(chunks, OpenSky.Instance);

        // Чанк сверху ушёл — снова солнце по рельефу.
        chunks.Remove(above.Position);
        engine.ChunkRemoved(above.Position);
        Assert.Equal(15, engine.GetSky(new BlockPos(5, 0, 5)));
        AssertMatchesReference(chunks, OpenSky.Instance);
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(-1, 0, 0, true)]
    [InlineData(0, 0, 1, true)]
    [InlineData(0, 0, -1, true)]
    [InlineData(0, -1, 0, true)]
    [InlineData(1, 0, 0, false)]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, -1, false)]
    [InlineData(0, -1, 0, false)]
    public void NewChunkInSun_LightsDarkNeighbor(int dx, int dy, int dz, bool empty)
    {
        // Сосед уже загружен и тёмный (сбоку — под «горой», снизу — под рельефом на высоте 20). Приходит чанк на солнце
        // (пустой или с камнем в углу): солнце должно перейти к соседу.
        var dark = new ChunkPos(dx, dy, dz);
        var sky = new HeightSky((x, z) => dy < 0 ? 20 : new BlockPos(x, 0, z).Chunk with { Y = 0 } == dark ? 1000 : -1000);
        var chunks = new ChunkMap();
        var engine = new LightEngine(_blocks, chunks, sky);
        var neighbor = new Chunk(dark);
        chunks.Set(neighbor);
        engine.ChunkAdded(neighbor);
        Assert.Equal(0, engine.GetSky(dark.Origin + new BlockPos(16, 16, 16)));

        var sunny = new Chunk(new ChunkPos(0, 0, 0));
        if (!empty)
            sunny.Set(new LocalPos(dx > 0 ? 0 : 31, 31, dz > 0 ? 0 : 31), _stone);
        chunks.Set(sunny);
        engine.ChunkAdded(sunny);

        // Блок соседа, прилегающий к солнечному чанку.
        static int Across(int d) => d > 0 ? 32 : d < 0 ? -1 : 16;
        Assert.Equal(dy < 0 ? 15 : 14, engine.GetSky(new BlockPos(Across(dx), Across(dy), Across(dz))));
        AssertMatchesReference(chunks, sky);
    }

    [Fact]
    public void AirUnderLoadedAir_StaysUniform()
    {
        // Чанк воздуха приходит под уже загруженный чанк воздуха на солнце: свет — одно значение, без массива.
        var chunks = new ChunkMap();
        var engine = new LightEngine(_blocks, chunks, OpenSky.Instance);
        foreach (var y in (int[])[1, 0])
        {
            var chunk = new Chunk(new ChunkPos(0, y, 0));
            chunks.Set(chunk);
            engine.ChunkAdded(chunk);
            Assert.True(chunk.Light.IsUniform);
            Assert.Equal(15, chunk.Light.GetSky(0));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomEdits_MatchFromScratch(int seed)
    {
        var random = new Random(seed);
        var sky = new HeightSky((x, z) => (x + z) % 3 == 0 ? 40 : 0);
        var (chunks, engine) = Create(sky, c => BuildRandomWorld(c, random));
        AssertMatchesReference(chunks, sky);

        BlockId[] palette = [BlockId.Air, BlockId.Air, _stone, _stone, _glass, _torch, _lamp, _glowshroom];
        for (var batch = 0; batch < 10; batch++)
        {
            for (var i = 0; i < 30; i++)
            {
                // Половина правок — у границ чанков (координаты −1, 0, 31, −32 и рядом), там ошибки вероятнее.
                var pos = new BlockPos(Coordinate(random), Coordinate(random), Coordinate(random));
                Set(chunks, engine, pos, palette[random.Next(palette.Length)]);
            }
            AssertMatchesReference(chunks, sky);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ChunkOrder_AndRemoval_MatchFromScratch(int seed)
    {
        var random = new Random(seed);
        var sky = new HeightSky((x, z) => x > 8 ? 50 : -100);
        var source = new ChunkMap();
        foreach (var position in Region())
            source.Set(new Chunk(position));
        BuildRandomWorld(source, random);

        var chunks = new ChunkMap();
        var engine = new LightEngine(_blocks, chunks, sky);
        foreach (var chunk in source.All.OrderBy(_ => random.Next()).ToList())
        {
            chunks.Set(chunk);
            engine.ChunkAdded(chunk);
            AssertMatchesReference(chunks, sky);
        }

        foreach (var position in Region().OrderBy(_ => random.Next()).Take(4).ToList())
        {
            chunks.Remove(position);
            engine.ChunkRemoved(position);
            AssertMatchesReference(chunks, sky);
        }
    }

    [Fact]
    public void TakeChanged_ListsChunksWhoseLightChanged()
    {
        var (chunks, engine) = Create(build: c => Fill(c, -32, -32, -32, 31, 31, 31, _stone));
        var changed = new List<ChunkPos>();
        engine.TakeChanged(changed);
        changed.Clear();

        // Факел в пустоте у угла (−1, −1, −1): свет уходит во все восемь чанков, если есть куда.
        Fill(chunks, -3, -3, -3, 2, 2, 2, BlockId.Air);
        Set(chunks, engine, new BlockPos(0, 0, 0), _torch);
        engine.TakeChanged(changed);
        Assert.Equal(Region().ToHashSet(), changed.ToHashSet());

        changed.Clear();
        engine.TakeChanged(changed);
        Assert.Empty(changed);

        // После выдачи те же чанки отмечаются заново.
        Set(chunks, engine, new BlockPos(0, 0, 0), BlockId.Air);
        engine.TakeChanged(changed);
        Assert.Equal(Region().ToHashSet(), changed.ToHashSet());

        // Правки внутри одного чанка подряд: он отмечен последним перед выдачей — и всё равно отмечается снова.
        changed.Clear();
        Fill(chunks, 10, 10, 10, 20, 20, 20, BlockId.Air);
        Set(chunks, engine, new BlockPos(15, 15, 15), _torch);
        engine.TakeChanged(changed);
        Assert.Equal([new ChunkPos(0, 0, 0)], changed);
        changed.Clear();
        Set(chunks, engine, new BlockPos(15, 15, 15), BlockId.Air);
        engine.TakeChanged(changed);
        Assert.Equal([new ChunkPos(0, 0, 0)], changed);
    }

    private static int Coordinate(Random random) => random.Next(2) == 0
        ? random.Next(-32, 32)
        : BorderCoordinates[random.Next(BorderCoordinates.Length)];

    private static readonly int[] BorderCoordinates = [-32, -31, -2, -1, 0, 1, 30, 31];

    /// <summary>Рельеф с холмами, навесами, нишами, стеклом и светящимися блоками.</summary>
    private void BuildRandomWorld(ChunkMap chunks, Random random)
    {
        for (var x = -32; x < 32; x++)
        for (var z = -32; z < 32; z++)
        {
            var height = random.Next(-10, 5);
            for (var y = -32; y < height; y++)
                chunks.TrySetBlock(new BlockPos(x, y, z), _stone);
        }

        for (var i = 0; i < 12; i++)
        {
            int x = random.Next(-32, 24), y = random.Next(-30, 30), z = random.Next(-32, 24);
            BlockId[] kinds = [_stone, _stone, BlockId.Air, _glass];
            var block = kinds[random.Next(kinds.Length)];
            Fill(chunks, x, y, z, x + random.Next(1, 9), y + random.Next(0, 2), z + random.Next(1, 9), block);
        }

        for (var i = 0; i < 6; i++)
        {
            BlockId[] sources = [_torch, _lamp, _glowshroom];
            var block = sources[random.Next(sources.Length)];
            chunks.TrySetBlock(new BlockPos(random.Next(-32, 32), random.Next(-32, 32), random.Next(-32, 32)), block);
        }
    }

    private void AssertMatchesReference(ChunkMap chunks, ISkyAbove sky)
    {
        var expected = Reference(chunks, sky);
        foreach (var chunk in chunks.All)
        {
            var reference = expected[chunk.Position];
            for (var index = 0; index < ChunkMath.Volume; index++)
            {
                var actual = chunk.Light.Get(index);
                if (actual != reference[index])
                {
                    var pos = chunk.Position.ToWorld(LocalPos.FromIndex(index));
                    Assert.Fail($"Свет в {pos} (блок {chunk.Blocks[index]}): небо {actual >> 4}, блоки {actual & 15}; " +
                        $"с нуля — небо {reference[index] >> 4}, блоки {reference[index] & 15}.");
                }
            }
        }
    }

    /// <summary>
    /// Свет «с нуля», независимо от <see cref="LightEngine"/>: все источники сразу, заливка по мировым координатам.
    /// Даёт наименьший свет, удовлетворяющий правилу.
    /// </summary>
    private Dictionary<ChunkPos, byte[]> Reference(ChunkMap chunks, ISkyAbove sky)
    {
        var result = chunks.All.ToDictionary(c => c.Position, _ => new byte[ChunkMath.Volume]);
        bool IsOpaque(BlockPos p) => _blocks.Get(chunks.GetBlock(p)).IsOpaque;

        foreach (var isSky in (bool[])[true, false])
        {
            var levels = new Dictionary<BlockPos, int>();
            var queue = new Queue<BlockPos>();
            foreach (var chunk in chunks.All)
            for (var index = 0; index < ChunkMath.Volume; index++)
            {
                var local = LocalPos.FromIndex(index);
                var pos = chunk.Position.ToWorld(local);
                var definition = _blocks.Get(chunk.Blocks[index]);
                var level = isSky
                    ? (!definition.IsOpaque && local.Y == 31 && !chunks.Contains(chunk.Position with { Y = chunk.Position.Y + 1 })
                        && sky.IsOpen(pos.X, pos.Z, pos.Y + 1) ? 15 : 0)
                    : definition.Light;
                if (level > 0)
                {
                    levels[pos] = level;
                    queue.Enqueue(pos);
                }
            }

            BlockPos[] steps = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
            while (queue.TryDequeue(out var pos))
            {
                var level = levels[pos];
                foreach (var step in steps)
                {
                    var next = pos + step;
                    if (!chunks.Contains(next.Chunk) || IsOpaque(next))
                        continue;
                    var nextLevel = isSky && step.Y == -1 && level == 15 ? 15 : level - 1;
                    if (nextLevel > levels.GetValueOrDefault(next))
                    {
                        levels[next] = nextLevel;
                        queue.Enqueue(next);
                    }
                }
            }

            foreach (var (pos, level) in levels)
                result[pos.Chunk][pos.Local.Index] |= (byte)(isSky ? level << 4 : level);
        }
        return result;
    }

    /// <summary>Солнце над незагруженным — по заданной естественной высоте столбца.</summary>
    private sealed class HeightSky(Func<int, int, int> top) : ISkyAbove
    {
        public bool IsOpen(int x, int z, int y) => top(x, z) <= y;
    }
}
