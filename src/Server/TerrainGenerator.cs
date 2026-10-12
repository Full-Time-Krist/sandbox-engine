using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Генератор рельефа по шуму. Детерминирован: один и тот же сид и чанк всегда дают одинаковые блоки.
/// </summary>
/// <remarks>
/// <para>
/// Высота поверхности — смесь трёх рельефов: равнины у уровня моря, холмы и горы. Какой из них где,
/// решает медленно меняющийся шум «типа местности»; переходы между рельефами плавные (smoothstep),
/// поэтому равнина поднимается в холмы, а холмы — в горы без обрывов.
/// Перед всеми шумами координаты искажаются ещё одним шумом (domain warp): без этого очертания
/// холмов слишком «круглые» и одинаковые.
/// </para>
/// <para>
/// Потокобезопасен: после настройки шумы только читаются, поэтому один генератор используют все фоновые потоки.
/// </para>
/// </remarks>
internal sealed class TerrainGenerator
{
    public const int SeaLevel = 0;

    /// <summary>Пределы высоты поверхности. Ниже — только камень, выше — только воздух.</summary>
    public const int MinSurface = -64;
    public const int MaxSurface = 192;

    private const int DirtDepth = 3;

    /// <summary>Поверхность не выше SeaLevel + BeachHeight — песок («пляж»; ниже уровня моря — дно будущих водоёмов).</summary>
    private const int BeachHeight = 2;

    /// <summary>Выше этой высоты вершины гор голые, из камня.</summary>
    private const int BareRockHeight = 130;

    private readonly BaseBlocks _blocks;
    private readonly FastNoiseLite _warp;
    private readonly FastNoiseLite _terrainType;
    private readonly FastNoiseLite _plains;
    private readonly FastNoiseLite _hills;
    private readonly FastNoiseLite _mountains;

    public int Seed { get; }

    public TerrainGenerator(BaseBlocks blocks, int seed)
    {
        _blocks = blocks;
        Seed = seed;

        // У каждого шума свой сид, производный от сида мира: иначе одинаковые шумы дали бы одинаковый рисунок.
        _warp = Noise(seed, 0, frequency: 1f / 300, octaves: 3);
        _warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        _warp.SetDomainWarpAmp(60);
        _warp.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);

        _terrainType = Noise(seed, 1, frequency: 1f / 700, octaves: 3);
        _plains = Noise(seed, 2, frequency: 1f / 150, octaves: 3);
        _hills = Noise(seed, 3, frequency: 1f / 200, octaves: 4);
        _mountains = Noise(seed, 4, frequency: 1f / 400, octaves: 5);
        _mountains.SetFractalType(FastNoiseLite.FractalType.Ridged);
    }

    private static FastNoiseLite Noise(int seed, int salt, float frequency, int octaves)
    {
        var noise = new FastNoiseLite(unchecked(seed * 31 + salt * 1_000_003));
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(frequency);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(octaves);
        return noise;
    }

    /// <summary>Высота поверхности: y верхнего блока плюс один. Всегда в [<see cref="MinSurface"/>; <see cref="MaxSurface"/>].</summary>
    public int SurfaceHeight(int x, int z) =>
        Math.Clamp((int)Math.Floor(RawSurfaceHeight(x, z)), MinSurface, MaxSurface);

    /// <summary>Высота поверхности до округления и ограничения. Отдельно — для тестов точности шума.</summary>
    internal double RawSurfaceHeight(int x, int z)
    {
        double wx = x, wz = z;
        _warp.DomainWarp(ref wx, ref wz);

        // Шумы FastNoiseLite выдают значения примерно в [−1; 1].
        var type = _terrainType.GetNoise(wx, wz);
        // Равнины — в основном от 1 до 13 блоков над морем; в низинах выходят к уровню моря, там пляжи.
        var plains = 7 + 7 * _plains.GetNoise(wx, wz);
        var hills = 10 + 22 * _hills.GetNoise(wx, wz);

        // Гребневой шум даёт острые хребты. Степень делает подножия пологими, а пики — резкими.
        var ridge = (_mountains.GetNoise(wx, wz) + 1) / 2;
        var mountains = 25 + 165 * Math.Pow(ridge, 1.6);

        // Тип местности: ниже −0,2 — равнины, около 0,1 — холмы, выше 0,45 — горы.
        var height = Lerp(plains, hills, SmoothStep(-0.2, 0.1, type));
        return Lerp(height, mountains, SmoothStep(0.2, 0.45, type));
    }

    public Chunk Generate(ChunkPos position)
    {
        var origin = position.Origin;

        // Вне диапазона рельефа шум не нужен: выше — воздух, ниже — камень (с запасом на слой земли).
        if (origin.Y >= MaxSurface)
            return new Chunk(position);
        if (origin.Y + ChunkMath.Size <= MinSurface - DirtDepth - 1)
            return SolidStone(position);

        var chunk = new Chunk(position);
        for (var lx = 0; lx < ChunkMath.Size; lx++)
        for (var lz = 0; lz < ChunkMath.Size; lz++)
        {
            var height = SurfaceHeight(origin.X + lx, origin.Z + lz);
            var (top, under) = SurfaceBlocks(height);

            var end = Math.Min(ChunkMath.Size, height - origin.Y);
            for (var ly = 0; ly < end; ly++)
            {
                var depth = height - 1 - (origin.Y + ly); // 0 — верхний блок
                var block = depth == 0 ? top : depth <= DirtDepth ? under : _blocks.Stone;
                chunk.Set(new LocalPos(lx, ly, lz), block);
            }
        }

        return chunk;
    }

    /// <summary>Верхний блок столбца и блоки под ним (до глубины <see cref="DirtDepth"/>).</summary>
    private (BlockId Top, BlockId Under) SurfaceBlocks(int height)
    {
        if (height <= SeaLevel + BeachHeight)
            return (_blocks.Sand, _blocks.Sand);
        if (height >= BareRockHeight)
            return (_blocks.Stone, _blocks.Stone);
        return (_blocks.Grass, _blocks.Dirt);
    }

    private Chunk SolidStone(ChunkPos position)
    {
        var blocks = new BlockId[ChunkMath.Volume];
        Array.Fill(blocks, _blocks.Stone);
        return Chunk.FromBlocks(position, blocks);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
