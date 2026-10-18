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
/// Деревья стоят на траве. Мир поделён на ячейки <see cref="TreeCellSize"/> × <see cref="TreeCellSize"/> столбцов, в каждой
/// не больше одного дерева; есть ли оно, где и какой высоты — зависит только от сида и координат ячейки (<see cref="TreeInCell"/>),
/// а густоту задаёт медленный шум «лесистости». Поэтому чанк сам достраивает части деревьев соседних ячеек, задевающие его,
/// и дерево на границе чанков одинаково с обеих сторон при любом порядке генерации.
/// </para>
/// <para>
/// Медная руда — небольшие жилы внутри каждого чанка, только на месте камня (а камень начинается глубже слоя земли).
/// Жила не выходит за свой чанк, поэтому соседей знать не нужно.
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
    internal const int BeachHeight = 2;

    /// <summary>Выше этой высоты вершины гор голые, из камня.</summary>
    internal const int BareRockHeight = 130;

    /// <summary>Сторона ячейки сетки деревьев: в ячейке не больше одного дерева.</summary>
    internal const int TreeCellSize = 6;

    /// <summary>Насколько крона выходит от ствола по горизонтали.</summary>
    internal const int TreeCanopyRadius = 2;

    /// <summary>
    /// Жил медной руды в чанке и шагов в жиле (от и до): около 0,2% камня — при проходке тоннелем жила попадается
    /// примерно раз в 30 блоков. Окончательно — при балансе петли (шаг 6.6).
    /// </summary>
    private const int OreVeinsPerChunk = 10;
    private const int OreVeinMinSize = 4;
    private const int OreVeinMaxSize = 10;

    /// <summary>Дерево: ствол в столбце (<paramref name="X"/>, <paramref name="Z"/>) от высоты <paramref name="Base"/> (над травой).</summary>
    internal readonly record struct Tree(int X, int Z, int Base, int TrunkHeight)
    {
        /// <summary>Верх кроны (включительно): над стволом ещё два слоя листвы.</summary>
        public int Top => Base + TrunkHeight + 1;
    }

    private readonly BaseBlocks _blocks;
    private readonly FastNoiseLite _warp;
    private readonly FastNoiseLite _terrainType;
    private readonly FastNoiseLite _plains;
    private readonly FastNoiseLite _hills;
    private readonly FastNoiseLite _mountains;
    private readonly FastNoiseLite _forest;

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
        _forest = Noise(seed, 5, frequency: 1f / 250, octaves: 2);
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
        {
            var stone = SolidStone(position);
            PlaceOre(stone);
            return stone;
        }

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

        PlaceOre(chunk);
        PlaceTrees(chunk);
        return chunk;
    }

    /// <summary>Дерево ячейки (<paramref name="cellX"/>, <paramref name="cellZ"/>) или null. Зависит только от сида и ячейки.</summary>
    internal Tree? TreeInCell(int cellX, int cellZ)
    {
        var hash = Hash(cellX, 0, cellZ, salt: 1);

        // Густота: на открытых местах редкие деревья, в лесах (шум выше ~0,1) — почти в каждой ячейке.
        var center = (X: cellX * TreeCellSize + TreeCellSize / 2, Z: cellZ * TreeCellSize + TreeCellSize / 2);
        var chance = 0.04 + 0.7 * SmoothStep(-0.1, 0.35, _forest.GetNoise(center.X, center.Z));
        if ((hash & 0xFFFF) / 65536.0 >= chance)
            return null;

        // Ствол — не у края ячейки: стволы соседних ячеек не ближе двух блоков друг от друга.
        var x = cellX * TreeCellSize + 1 + (int)((hash >> 16) % (TreeCellSize - 2));
        var z = cellZ * TreeCellSize + 1 + (int)((hash >> 24) % (TreeCellSize - 2));
        var surface = SurfaceHeight(x, z);
        var (top, _) = SurfaceBlocks(surface);
        if (top != _blocks.Grass)
            return null;
        return new Tree(x, z, surface, TrunkHeight: 4 + (int)((hash >> 32) % 3));
    }

    /// <summary>
    /// Блок дерева в точке или null. Крона — как у дуба Minecraft: два широких слоя 5×5 у верха ствола (углы — через раз),
    /// над ними 3×3 и крест. Ствол в листве не учитывается — его ставят отдельно, поверх.
    /// </summary>
    internal BlockId? TreeBlock(Tree tree, int x, int y, int z)
    {
        var (dx, dz) = (Math.Abs(x - tree.X), Math.Abs(z - tree.Z));
        if (dx == 0 && dz == 0 && y >= tree.Base && y < tree.Base + tree.TrunkHeight)
            return _blocks.Log;

        var layer = y - (tree.Base + tree.TrunkHeight); // 0 — слой сразу над стволом
        var leaves = layer switch
        {
            -2 or -1 => dx <= 2 && dz <= 2 && !(dx == 2 && dz == 2 && (Hash(x, y, z, salt: 2) & 1) == 0),
            0 => dx <= 1 && dz <= 1,
            1 => dx + dz <= 1,
            _ => false,
        };
        return leaves ? _blocks.Leaves : null;
    }

    /// <summary>
    /// Столбец для точки появления: ближайший к (<paramref name="x"/>, <paramref name="z"/>) по спирали, где на высоте тела
    /// (два блока над поверхностью) нет дерева — иначе игрок появился бы в стволе или листве.
    /// </summary>
    internal (int X, int Z) SpawnColumnNear(int x, int z)
    {
        for (var radius = 0; radius <= 16; radius++)
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius)
                continue; // только кольцо этого радиуса
            var surface = SurfaceHeight(x + dx, z + dz);
            if (!HasTreeBlock(x + dx, z + dz, surface, surface + 1))
                return (x + dx, z + dz);
        }
        return (x, z); // в сплошном лесу 33 × 33 без просвета — как раньше
    }

    /// <summary>Есть ли блок какого-нибудь дерева в столбце (<paramref name="x"/>, <paramref name="z"/>) на высотах [<paramref name="fromY"/>; <paramref name="toY"/>].</summary>
    internal bool HasTreeBlock(int x, int z, int fromY, int toY)
    {
        foreach (var tree in TreesAround(x - TreeCanopyRadius, z - TreeCanopyRadius, x + TreeCanopyRadius, z + TreeCanopyRadius))
        {
            for (var y = fromY; y <= toY; y++)
            {
                if (TreeBlock(tree, x, y, z) != null)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Деревья ячеек, чьи стволы лежат в прямоугольнике столбцов [min; max] (по порядку ячеек — порядок важен для одинаковости).</summary>
    private IEnumerable<Tree> TreesAround(int minX, int minZ, int maxX, int maxZ)
    {
        for (var cellX = FloorDiv(minX, TreeCellSize); cellX <= FloorDiv(maxX, TreeCellSize); cellX++)
        for (var cellZ = FloorDiv(minZ, TreeCellSize); cellZ <= FloorDiv(maxZ, TreeCellSize); cellZ++)
        {
            if (TreeInCell(cellX, cellZ) is { } tree)
                yield return tree;
        }
    }

    /// <summary>
    /// Ставит в чанк его части всех деревьев, чьи кроны его задевают. Листва — только в воздух и сначала у всех деревьев,
    /// стволы — потом и поверх листвы: так результат не зависит от того, в каком порядке перекрываются кроны.
    /// </summary>
    private void PlaceTrees(Chunk chunk)
    {
        var origin = chunk.Position.Origin;
        var trees = TreesAround(origin.X - TreeCanopyRadius, origin.Z - TreeCanopyRadius,
                origin.X + ChunkMath.Size - 1 + TreeCanopyRadius, origin.Z + ChunkMath.Size - 1 + TreeCanopyRadius)
            .Where(tree => tree.Top >= origin.Y && tree.Base < origin.Y + ChunkMath.Size)
            .ToArray();

        foreach (var placeTrunks in (ReadOnlySpan<bool>)[false, true])
        foreach (var tree in trees)
        {
            var minY = Math.Max(tree.Base, origin.Y);
            var maxY = Math.Min(tree.Top, origin.Y + ChunkMath.Size - 1);
            for (var x = Math.Max(tree.X - TreeCanopyRadius, origin.X); x <= Math.Min(tree.X + TreeCanopyRadius, origin.X + ChunkMath.Size - 1); x++)
            for (var z = Math.Max(tree.Z - TreeCanopyRadius, origin.Z); z <= Math.Min(tree.Z + TreeCanopyRadius, origin.Z + ChunkMath.Size - 1); z++)
            for (var y = minY; y <= maxY; y++)
            {
                if (TreeBlock(tree, x, y, z) is not { } block || (block == _blocks.Log) != placeTrunks)
                    continue;
                var local = new LocalPos(x - origin.X, y - origin.Y, z - origin.Z);
                if (placeTrunks || chunk.Get(local).IsAir)
                    chunk.Set(local, block);
            }
        }
    }

    /// <summary>Жилы медной руды: случайные блуждания внутри чанка, заменяющие только камень.</summary>
    private void PlaceOre(Chunk chunk)
    {
        var p = chunk.Position;
        var state = Hash(p.X, p.Y, p.Z, salt: 3);
        for (var vein = 0; vein < OreVeinsPerChunk; vein++)
        {
            int x = Next(ChunkMath.Size), y = Next(ChunkMath.Size), z = Next(ChunkMath.Size);
            var size = OreVeinMinSize + Next(OreVeinMaxSize - OreVeinMinSize + 1);
            for (var step = 0; step < size; step++)
            {
                var local = new LocalPos(x, y, z);
                if (chunk.Get(local) == _blocks.Stone)
                    chunk.Set(local, _blocks.CopperOre);
                // Шаг в одну из шести сторон, не выходя из чанка.
                switch (Next(6))
                {
                    case 0: x = Math.Min(x + 1, ChunkMath.Size - 1); break;
                    case 1: x = Math.Max(x - 1, 0); break;
                    case 2: y = Math.Min(y + 1, ChunkMath.Size - 1); break;
                    case 3: y = Math.Max(y - 1, 0); break;
                    case 4: z = Math.Min(z + 1, ChunkMath.Size - 1); break;
                    default: z = Math.Max(z - 1, 0); break;
                }
            }
        }

        int Next(int bound)
        {
            state = SplitMix(state);
            return (int)(state % (ulong)bound);
        }
    }

    /// <summary>Хеш координат с сидом мира: одинаковые входы — одинаковый результат на любой машине и в любом потоке.</summary>
    private ulong Hash(int x, int y, int z, int salt) =>
        SplitMix(SplitMix(SplitMix(SplitMix((ulong)(uint)Seed ^ ((ulong)(uint)salt << 32)) ^ (uint)x) ^ (uint)y) ^ (uint)z);

    /// <summary>Шаг SplitMix64: хорошо перемешивает биты, достаточно для мира (не для криптографии).</summary>
    private static ulong SplitMix(ulong value)
    {
        value += 0x9E3779B97F4A7C15;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

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
