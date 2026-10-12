using Shared.Blocks;
using Shared.Voxels;

namespace Server.Tests;

public class TerrainGeneratorTests
{
    private readonly BaseBlocks _blocks;

    public TerrainGeneratorTests()
    {
        var registry = new BlockRegistry();
        _blocks = BaseBlocks.Register(registry);
        registry.Freeze();
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
        Assert.True(map.GetBlock(new BlockPos(x, height, z)).IsAir, "Над поверхностью не воздух");
        Assert.Equal(_blocks.Stone, map.GetBlock(new BlockPos(x, height - 10, z)));
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
        Assert.All(below.Blocks.ToArray(), b => Assert.Equal(_blocks.Stone, b));
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
