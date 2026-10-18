using Shared.Blocks;
using Shared.Content;
using Shared.Voxels;

namespace Server.Tests;

public class TerrainGeneratorTests
{
    private readonly BaseBlocks _blocks;

    public TerrainGeneratorTests()
    {
        var registry = GameContent.Load().Blocks;
        _blocks = BaseBlocks.From(registry);
    }

    private TerrainGenerator Generator(int seed = 1) => new(_blocks, seed);

    /// <summary>Столбцы на сетке с шагом <paramref name="step"/> в квадрате со стороной 2 × <paramref name="radius"/>.</summary>
    private static IEnumerable<(int X, int Z)> Grid(int radius, int step)
    {
        for (var x = -radius; x < radius; x += step)
        for (var z = -radius; z < radius; z += step)
            yield return (x, z);
    }

    [Fact]
    public void SameSeed_GivesSameChunks()
    {
        // Два независимых генератора: одинаковый результат не может прийти из общего состояния.
        var a = Generator(seed: 7);
        var b = Generator(seed: 7);

        foreach (var position in new[] { new ChunkPos(0, 0, 0), new ChunkPos(-5, 1, 9), new ChunkPos(30, -1, -30) })
            Assert.True(a.Generate(position).Blocks.SequenceEqual(b.Generate(position).Blocks), $"Чанк {position} отличается");
    }

    [Fact]
    public void DifferentSeed_GivesDifferentTerrain()
    {
        var a = Generator(seed: 1);
        var b = Generator(seed: 2);

        var differing = Grid(radius: 512, step: 32).Count(c => a.SurfaceHeight(c.X, c.Z) != b.SurfaceHeight(c.X, c.Z));

        // Совпасть могут отдельные столбцы, но не большинство.
        Assert.True(differing > 900, $"Отличаются только {differing} столбцов из 1024");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-37, 85)]
    [InlineData(1200, -900)]
    public void Column_HasSurfaceBlockOnTop_AirAbove_StoneDeep(int x, int z)
    {
        var generator = Generator();
        var height = generator.SurfaceHeight(x, z);
        var map = new ChunkMap();
        for (var y = height - 40; y <= height + 40; y += ChunkMath.Size)
            map.Set(generator.Generate(new BlockPos(x, y, z).Chunk));

        var surface = map.GetBlock(new BlockPos(x, height - 1, z));
        Assert.Contains(surface, new[] { _blocks.Grass, _blocks.Sand, _blocks.Stone });
        // Над поверхностью — воздух, если здесь не стоит дерево (ствол или крона, своего или соседнего).
        var above = map.GetBlock(new BlockPos(x, height, z));
        if (generator.HasTreeBlock(x, z, height, height))
            Assert.Contains(above, new[] { _blocks.Log, _blocks.Leaves });
        else
            Assert.True(above.IsAir, "Над поверхностью не воздух");
        Assert.Contains(map.GetBlock(new BlockPos(x, height - 10, z)), new[] { _blocks.Stone, _blocks.CopperOre });
    }

    [Fact]
    public void HighestAndLowestColumns_AreComplete()
    {
        // Быстрые пути (чанк целиком выше или ниже рельефа) не должны срезать вершины гор и дно низин.
        var generator = Generator();
        var columns = Grid(radius: 2048, step: 16).Select(c => (c.X, c.Z, H: generator.SurfaceHeight(c.X, c.Z))).ToList();

        foreach (var (x, z, height) in new[] { columns.MaxBy(c => c.H), columns.MinBy(c => c.H) })
        {
            var top = new BlockPos(x, height - 1, z);
            var chunk = generator.Generate(top.Chunk);
            Assert.False(chunk.Get(top.Local).IsAir, $"Нет верхнего блока на высоте {height - 1} в ({x}, {z})");
            Assert.True(generator.Generate(new BlockPos(x, height, z).Chunk).Get(new BlockPos(x, height, z).Local).IsAir);
        }
    }

    [Fact]
    public void SurfaceHeight_StaysWithinLimits()
    {
        var generator = Generator();

        foreach (var (x, z) in Grid(radius: 4096, step: 64))
            Assert.InRange(generator.SurfaceHeight(x, z), TerrainGenerator.MinSurface, TerrainGenerator.MaxSurface);
    }

    [Fact]
    public void Terrain_HasPlainsHillsAndMountains()
    {
        // Смешивание рельефов должно давать все три вида местности, а не одну «среднюю» холмистость.
        var generator = Generator();
        var heights = Grid(radius: 2048, step: 32).Select(c => generator.SurfaceHeight(c.X, c.Z)).ToList();

        double Share(Func<int, bool> predicate) => heights.Count(predicate) / (double)heights.Count;
        Assert.InRange(Share(h => h is > TerrainGenerator.SeaLevel and < 16), 0.3, 0.9);   // равнины
        Assert.InRange(Share(h => h is >= 25 and < 60), 0.03, 0.4);                         // холмы
        Assert.InRange(Share(h => h >= 80), 0.02, 0.3);                                     // горы
        Assert.InRange(Share(h => h <= TerrainGenerator.SeaLevel + 2), 0.01, 0.2);          // пляжи и низины
    }

    [Fact]
    public void ChunksOutsideTerrainRange_AreUniform()
    {
        var generator = Generator();

        var above = generator.Generate(new ChunkPos(3, TerrainGenerator.MaxSurface >> ChunkMath.Bits, -2));
        var below = generator.Generate(new ChunkPos(3, (TerrainGenerator.MinSurface >> ChunkMath.Bits) - 2, -2));

        Assert.True(above.IsEmpty);
        Assert.Equal(ChunkMath.Volume, below.NonAirCount);
        // Глубоко — камень с жилами руды: быстрый путь для сплошного камня руду не пропускает.
        Assert.All(below.Blocks.ToArray(), b => Assert.Contains(b, new[] { _blocks.Stone, _blocks.CopperOre }));
        Assert.Contains(_blocks.CopperOre, below.Blocks.ToArray());
    }

    // --- Деревья и руда ---

    /// <summary>Деревья в квадрате ячеек вокруг начала мира.</summary>
    private static List<TerrainGenerator.Tree> Trees(TerrainGenerator generator, int cells = 40)
    {
        var trees = new List<TerrainGenerator.Tree>();
        for (var cx = -cells; cx < cells; cx++)
        for (var cz = -cells; cz < cells; cz++)
        {
            if (generator.TreeInCell(cx, cz) is { } tree)
                trees.Add(tree);
        }
        return trees;
    }

    [Fact]
    public void Trees_ForestsAndClearings_OnlyOnGrass()
    {
        var generator = Generator();
        var near = Trees(generator);
        // 80 × 80 ячеек у начала мира: деревья есть, но не в каждой ячейке.
        Assert.InRange(near.Count, 300, 80 * 80 * 0.8);
        foreach (var tree in near.Take(30))
        {
            var under = new BlockPos(tree.X, tree.Base - 1, tree.Z);
            Assert.Equal(_blocks.Grass, generator.Generate(under.Chunk).Get(under.Local));
        }

        // Широко (±2000 блоков, каждая третья ячейка), чтобы попали пляжи и голые горы: ни одного дерева там.
        var (beaches, rocks, trees) = (0, 0, 0);
        for (var cx = -330; cx < 330; cx += 3)
        for (var cz = -330; cz < 330; cz += 3)
        {
            var h = generator.SurfaceHeight(cx * TerrainGenerator.TreeCellSize, cz * TerrainGenerator.TreeCellSize);
            beaches += h <= TerrainGenerator.SeaLevel + TerrainGenerator.BeachHeight ? 1 : 0;
            rocks += h >= TerrainGenerator.BareRockHeight ? 1 : 0;
            if (generator.TreeInCell(cx, cz) is not { } tree)
                continue;
            trees++;
            Assert.InRange(tree.Base, TerrainGenerator.SeaLevel + TerrainGenerator.BeachHeight + 1, TerrainGenerator.BareRockHeight - 1);
        }
        Assert.True(beaches > 0 && rocks > 0 && trees > 0, $"Выборка без пляжей ({beaches}), гор ({rocks}) или деревьев ({trees})");
    }

    /// <summary>
    /// Дерево на границе чанков: его ствол в одном чанке, а часть кроны — в соседнем, и соседний чанк строит её сам,
    /// хотя генерируется отдельно. Проверяем все блоки кроны, попавшие в воздух над рельефом.
    /// </summary>
    [Fact]
    public void TreeAcrossChunkBorder_IsBuiltByBothChunks()
    {
        var generator = Generator();
        // Трудный случай: ячейка дерева целиком в одном чанке, а крона заходит в соседний. Так бывает, где граница ячеек
        // совпадает с границей чанков (каждые 96 блоков): соседний чанк сам свою ячейку с этим деревом не задевает и должен
        // найти дерево по кроне. Если бы ячейка пересекала границу, тест прошёл бы и при поиске деревьев без запаса на крону.
        static bool CellInOneChunk(int x) =>
            Math.Floor(x / (double)TerrainGenerator.TreeCellSize) * TerrainGenerator.TreeCellSize is var start
            && ((int)start >> ChunkMath.Bits) == (((int)start + TerrainGenerator.TreeCellSize - 1) >> ChunkMath.Bits);
        var trees = Trees(generator, cells: 120);
        // Проверяем только точки, куда блок может поставить лишь это дерево: в лесу крона соседа часто занимает те же
        // места, и тогда листья были бы на месте и без кроны проверяемого дерева (так тест сначала не ловил поломку).
        bool OnlyThisTree(TerrainGenerator.Tree tree, int x, int y, int z) =>
            trees.All(other => other == tree || generator.TreeBlock(other, x, y, z) == null);
        bool HasUniqueAcross(TerrainGenerator.Tree t)
        {
            var x = t.X + TerrainGenerator.TreeCanopyRadius;
            for (var y = t.Base; y <= t.Top; y++)
            for (var z = t.Z - TerrainGenerator.TreeCanopyRadius; z <= t.Z + TerrainGenerator.TreeCanopyRadius; z++)
            {
                if (generator.TreeBlock(t, x, y, z) != null && y >= generator.SurfaceHeight(x, z) && OnlyThisTree(t, x, y, z))
                    return true;
            }
            return false;
        }
        var tree = trees.First(t => CellInOneChunk(t.X)
            && ((t.X + TerrainGenerator.TreeCanopyRadius) >> ChunkMath.Bits) != (t.X >> ChunkMath.Bits)
            && HasUniqueAcross(t));
        var map = new ChunkMap();
        for (var y = tree.Base; y <= tree.Top; y++)
        for (var dx = -TerrainGenerator.TreeCanopyRadius; dx <= TerrainGenerator.TreeCanopyRadius; dx++)
        {
            var chunk = new BlockPos(tree.X + dx, y, tree.Z).Chunk;
            if (map.Get(chunk) == null)
                map.Set(generator.Generate(chunk));
        }

        var checkedAcross = 0;
        for (var y = tree.Base; y <= tree.Top; y++)
        for (var x = tree.X - TerrainGenerator.TreeCanopyRadius; x <= tree.X + TerrainGenerator.TreeCanopyRadius; x++)
        for (var z = tree.Z - TerrainGenerator.TreeCanopyRadius; z <= tree.Z + TerrainGenerator.TreeCanopyRadius; z++)
        {
            if (generator.TreeBlock(tree, x, y, z) is not { } expected || y < generator.SurfaceHeight(x, z)
                || !OnlyThisTree(tree, x, y, z))
                continue;
            var actual = map.GetBlock(new BlockPos(x, y, z));
            Assert.True(actual == expected, $"({x}, {y}, {z}): {actual}, ждали {expected}");
            if (x >> ChunkMath.Bits != tree.X >> ChunkMath.Bits)
                checkedAcross++;
        }
        Assert.True(checkedAcross > 0, "Ни одного блока кроны в соседнем чанке — тест ничего не проверил");
    }

    [Fact]
    public void Generation_DoesNotDependOnOrder()
    {
        // Порядок генерации чанков в игре случаен (фоновые потоки): один генератор в одном порядке, другой — в обратном.
        var positions = Enumerable.Range(-2, 4).SelectMany(x => Enumerable.Range(-2, 4).Select(z => new ChunkPos(x, 0, z))).ToList();
        var first = positions.Select(Generator().Generate).ToList();
        var second = Enumerable.Reverse(positions).Select(Generator().Generate).Reverse().ToList();

        for (var i = 0; i < positions.Count; i++)
            Assert.True(first[i].Blocks.SequenceEqual(second[i].Blocks), $"Чанк {positions[i]} отличается");
    }

    [Fact]
    public void CopperOre_OnlyWhereStoneWouldBe()
    {
        var generator = Generator();
        var ore = 0;
        foreach (var (cx, cz) in Grid(radius: 4, step: 1))
        {
            var chunk = generator.Generate(new ChunkPos(cx, -1, cz));
            var origin = chunk.Position.Origin;
            for (var i = 0; i < ChunkMath.Volume; i++)
            {
                if (chunk.Blocks[i] != _blocks.CopperOre)
                    continue;
                ore++;
                var local = LocalPos.FromIndex(i);
                var x = origin.X + local.X;
                var z = origin.Z + local.Z;
                // Камень начинается глубже верхнего блока и слоя земли (3 блока).
                Assert.True(origin.Y + local.Y < generator.SurfaceHeight(x, z) - 4, $"Руда не в камне: ({x}, {origin.Y + local.Y}, {z})");
            }
        }
        Assert.True(ore > 0, "Руды нет совсем");
    }

    [Fact]
    public void SpawnColumn_AvoidsTrees()
    {
        var generator = Generator();
        var tree = Trees(generator)[0];

        var (x, z) = generator.SpawnColumnNear(tree.X, tree.Z);

        Assert.NotEqual((tree.X, tree.Z), (x, z));
        var surface = generator.SurfaceHeight(x, z);
        Assert.False(generator.HasTreeBlock(x, z, surface, surface + 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    [InlineData(100_000_000)]
    public void FarFromCenter_NoiseKeepsPrecision(int far)
    {
        // Координаты шума в double. Во float шаг между соседними числами на 10⁸ — 8 блоков, и у соседних
        // столбцов шум выдавал бы одно и то же значение: рельеф слипся бы в ступени. На миллионе блоков float
        // ещё справляется, поэтому проверяем и дальше: мир по int тянется до двух миллиардов.
        // Целые высоты для проверки не годятся (равнины ровные и так), поэтому сравниваем высоту до округления.
        var generator = Generator();

        var heights = Enumerable.Range(0, 256).Select(i => generator.RawSurfaceHeight(far + i, far)).ToList();
        var equalNeighbours = heights.Zip(heights.Skip(1), (a, b) => a == b).Count(equal => equal);

        Assert.True(equalNeighbours < 5, $"У {equalNeighbours} пар соседних столбцов одинаковая высота");
    }
}
