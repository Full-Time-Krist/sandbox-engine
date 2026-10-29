using Server.Entities;
using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Tests.Entities;

/// <summary>
/// Поиск пути мобов (шаг 9.3) на собранном вручную мире: пол — камень на y = −1, ноги стоят на y = 0; мир — от −32 до 31
/// по каждой оси. Ходок — тело зомби (0,6 × 1,95).
/// </summary>
public class PathFinderTests
{
    private static readonly Walker Zombie = new(0.6, 1.95, 2.3f, 2.3f);

    private readonly BlockRegistry _blocks = new();
    private readonly ChunkMap _chunks = new();
    private readonly BlockId _stone;
    private readonly BlockId _slab;

    public PathFinderTests()
    {
        _stone = _blocks.Register("test:stone", isOpaque: true).Id;
        _slab = _blocks.Register("test:slab", isOpaque: false, shape: BlockShape.Slab).Id;
        _blocks.Freeze();
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            _chunks.Set(new Chunk(new ChunkPos(x, y, z)));
        for (var x = -32; x <= 31; x++)
        for (var z = -32; z <= 31; z++)
            Set(x, -1, z, _stone);
    }

    private void Set(int x, int y, int z, BlockId block) => Assert.True(_chunks.TrySetBlock(new BlockPos(x, y, z), block));

    /// <summary>Стена по X = <paramref name="x"/> от z1 до z2, высотой <paramref name="height"/> от пола.</summary>
    private void Wall(int x, int z1, int z2, int height = 3)
    {
        for (var z = z1; z <= z2; z++)
        for (var y = 0; y < height; y++)
            Set(x, y, z, _stone);
    }

    private static Vector3d Feet(double x, double y, double z) => new(x + 0.5, y, z + 0.5);

    private MobPath Find(Vector3d from, Vector3d to, int maxNodes = PathFinder.MaxNodes) =>
        PathFinder.Find(Zombie, from, to, _chunks, _blocks, maxNodes) ?? throw new Xunit.Sdk.XunitException("пути нет совсем");

    /// <summary>
    /// Путь пригоден для ходьбы: каждая точка — место, где можно стоять; соседние точки — в соседних столбцах; подъём не
    /// выше блока, спуск не глубже <see cref="PathFinder.MaxDrop"/>; по диагонали — только по ровному.
    /// </summary>
    private void AssertWalkable(Vector3d from, MobPath path)
    {
        var previous = from;
        foreach (var point in path.Points)
        {
            var cell = BlockPos.Containing(point + new Vector3d(0, 1e-3, 0));
            Assert.True(PathFinder.Standable(Zombie, cell, _chunks, _blocks, out var floor), $"на {point} не устоять");
            Assert.Equal(floor, point.Y, 9);
            var (dx, dz) = (Math.Abs(Math.Floor(point.X) - Math.Floor(previous.X)), Math.Abs(Math.Floor(point.Z) - Math.Floor(previous.Z)));
            Assert.True(dx <= 1 && dz <= 1 && dx + dz > 0, $"скачок {previous} → {point}");
            Assert.InRange(point.Y - previous.Y, -PathFinder.MaxDrop, PlayerPhysics.StepHeight);
            if (dx + dz == 2)
                Assert.Equal(previous.Y, point.Y);
            previous = point;
        }
    }

    [Fact]
    public void OpenFloor_StraightPath_ToGoal()
    {
        var from = Feet(0, 0, 0);
        var path = Find(from, Feet(10, 0, 0));

        Assert.True(path.Complete);
        Assert.Equal(10, path.Points.Length);
        Assert.Equal(Feet(10, 0, 0), path.Points[^1]);
        AssertWalkable(from, path);
    }

    /// <summary>Стена между мобом и целью — путь в обход через её конец, ни одна точка не в стене и не над ней.</summary>
    [Fact]
    public void Wall_PathGoesAround()
    {
        Wall(5, -6, 6);
        var from = Feet(0, 0, 0);
        var path = Find(from, Feet(10, 0, 0));

        Assert.True(path.Complete);
        AssertWalkable(from, path);
        Assert.Contains(path.Points, p => Math.Abs(Math.Floor(p.Z)) > 6); // обошёл конец стены
        Assert.DoesNotContain(path.Points, p => Math.Floor(p.X) == 5 && Math.Abs(Math.Floor(p.Z)) <= 6);
        Assert.All(path.Points, p => Assert.Equal(0, p.Y));
    }

    /// <summary>Стенка в блок — уступ: через неё прямо, а не в обход.</summary>
    [Fact]
    public void LowWall_IsSteppedOver()
    {
        Wall(5, -10, 10, height: 1);
        var from = Feet(0, 0, 0);
        var path = Find(from, Feet(10, 0, 0));

        Assert.True(path.Complete);
        AssertWalkable(from, path);
        Assert.Contains(Feet(5, 1, 0), path.Points);
        Assert.Equal(10, path.Points.Length);
    }

    /// <summary>Лестница с уступами в блок и в полблока (плита) — вверх на 4 блока; ступень в 2 блока — уже не подняться.</summary>
    [Fact]
    public void Stairs_ByBlocksAndSlabs_Climbed_TwoBlockStep_IsNot()
    {
        // Ступени по X = 2..5: высота 0,5 (плита), 1, 2, 3, 4 (сплошные столбы с плитой там, где нужна половина).
        Set(2, 0, 0, _slab);
        for (var y = 0; y < 1; y++) Set(3, y, 0, _stone);
        for (var y = 0; y < 2; y++) Set(4, y, 0, _stone);
        for (var y = 0; y < 2; y++) Set(5, y, 0, _stone);
        Set(5, 2, 0, _slab);
        for (var y = 0; y < 3; y++) Set(6, y, 0, _stone);
        // Узкий проход: стены по бокам лестницы, чтобы обойти её было нельзя.
        for (var x = 1; x <= 8; x++)
        for (var y = 0; y < 6; y++)
        {
            Set(x, y, -1, _stone);
            Set(x, y, 1, _stone);
        }

        var from = Feet(0, 0, 0);
        var path = Find(from, Feet(6, 3, 0));
        Assert.True(path.Complete);
        AssertWalkable(from, path);
        Assert.Equal([Feet(1, 0, 0), Feet(2, 0.5, 0), Feet(3, 1, 0), Feet(4, 2, 0), Feet(5, 2.5, 0), Feet(6, 3, 0)], path.Points);

        // Ступень в два блока над верхом лестницы: на неё не подняться ни отсюда, ни с пола — путь до ближайшего, неполный.
        for (var y = 0; y < 5; y++) Set(7, y, 0, _stone);
        var blocked = Find(from, Feet(7, 5, 0));
        Assert.False(blocked.Complete);
        Assert.DoesNotContain(blocked.Points, p => p.X > 7);
    }

    /// <summary>Проём в стене: высотой 2 — проходит насквозь; высотой 1 (зомби 1,95) — нет, и обойти негде — путь неполный.</summary>
    [Fact]
    public void Doorway_TwoHigh_Passes_OneHigh_DoesNot()
    {
        Wall(5, -32, 31, height: 4);
        Set(5, 0, 3, BlockId.Air);
        Set(5, 1, 3, BlockId.Air); // проём 1 × 2 в z = 3
        var from = Feet(0, 0, 0);

        var path = Find(from, Feet(10, 0, 0));
        Assert.True(path.Complete);
        AssertWalkable(from, path);
        Assert.Contains(Feet(5, 0, 3), path.Points);

        Set(5, 1, 3, _stone); // проём в блок высотой
        var blocked = Find(from, Feet(10, 0, 0));
        Assert.False(blocked.Complete);
        Assert.All(blocked.Points, p => Assert.True(p.X < 5));
    }

    /// <summary>Спрыгнуть можно с 3 блоков, с 4 — нет (с площадки 4 блока вниз не спуститься, и путь неполный).</summary>
    [Fact]
    public void Drop_UpToThreeBlocks()
    {
        // Площадка 5 × 5 на высоте h, с неё — к цели на полу.
        void Platform(int height)
        {
            for (var x = -2; x <= 2; x++)
            for (var z = -2; z <= 2; z++)
            for (var y = 0; y < 5; y++)
                Set(x, y, z, y < height ? _stone : BlockId.Air);
        }

        Platform(3);
        var high = Feet(0, 3, 0);
        var down = Find(high, Feet(6, 0, 0));
        Assert.True(down.Complete);
        AssertWalkable(high, down);

        Platform(4);
        var tooHigh = Feet(0, 4, 0);
        var stuck = PathFinder.Find(Zombie, tooHigh, Feet(6, 0, 0), _chunks, _blocks);
        Assert.False(stuck?.Complete ?? false);
        Assert.All(stuck?.Points ?? [], p => Assert.Equal(4, p.Y));
    }

    /// <summary>
    /// Подъём на уступ — сначала вверх на месте: низкий потолок над клеткой перед ступенькой не даёт подняться, и на
    /// ступеньку в узком коридоре не попасть. Без потолка — поднимается.
    /// </summary>
    [Fact]
    public void StepUp_UnderLowCeiling_NotTaken()
    {
        for (var x = 0; x <= 5; x++)
        for (var y = 0; y < 5; y++)
        {
            Set(x, y, -1, _stone);
            Set(x, y, 1, _stone);
        }
        Set(3, 0, 0, _stone);    // ступенька
        Set(2, 2, 0, _stone);    // потолок над клеткой перед ней: стоять под ним можно (1,95 < 2), подняться — нет
        Set(-1, 0, 0, _stone);
        Set(-1, 1, 0, _stone);   // коридор закрыт сзади
        var from = Feet(0, 0, 0);

        var blocked = Find(from, Feet(4, 1, 0));
        Assert.False(blocked.Complete);
        Assert.All(blocked.Points, p => Assert.True(p.X < 3));

        Set(2, 2, 0, BlockId.Air);
        Assert.True(Find(from, Feet(4, 1, 0)).Complete);
    }

    /// <summary>
    /// Спуск с уступа — шаг над краем на своей высоте: нависающий над нижней клеткой блок, в который упёрлась бы голова,
    /// не даёт спуститься, хотя стоять внизу под ним можно.
    /// </summary>
    [Fact]
    public void StepDown_UnderOverhang_NotTaken()
    {
        for (var x = -1; x <= 5; x++)
        for (var y = 0; y < 5; y++)
        {
            Set(x, y, -1, _stone);
            Set(x, y, 1, _stone);
        }
        Set(-1, 0, 0, _stone);
        Set(-1, 1, 0, _stone);
        Set(-1, 2, 0, _stone);
        for (var x = 0; x <= 1; x++)
            Set(x, 0, 0, _stone); // ступень высотой 1 у x 0–1, внизу — x 2 и дальше
        Set(2, 2, 0, _stone);     // нависает над нижней клеткой x 2 на высоте 2: голова сверху (до 2,95) упрётся
        var from = Feet(0, 1, 0);

        var blocked = Find(from, Feet(4, 0, 0));
        Assert.False(blocked.Complete);
        Assert.All(blocked.Points, p => Assert.True(p.X < 2));

        Set(2, 2, 0, BlockId.Air);
        Assert.True(Find(from, Feet(4, 0, 0)).Complete);
    }

    /// <summary>
    /// Подъём не выше уступа и на неполных блоках: плита на блоке рядом с полом — 1,5 блока, не подняться (а плита на
    /// полу и блок — можно).
    /// </summary>
    [Fact]
    public void StepUp_SlabOnBlock_IsTooHigh()
    {
        for (var x = 0; x <= 4; x++)
        for (var y = 0; y < 4; y++)
        {
            Set(x, y, -1, _stone);
            Set(x, y, 1, _stone);
        }
        Set(-1, 0, 0, _stone);
        Set(-1, 1, 0, _stone);
        Set(2, 0, 0, _stone);
        Set(2, 1, 0, _slab); // уступ 1,5 от пола x 1
        var from = Feet(0, 0, 0);

        var blocked = Find(from, Feet(3, 0, 0));
        Assert.False(blocked.Complete);
        Assert.All(blocked.Points, p => Assert.True(p.X < 2));

        Set(1, 0, 0, _slab); // теперь ступени по полблока: 0 → 0,5 → 1,5
        var path = Find(from, Feet(2, 1.5, 0));
        Assert.True(path.Complete);
        AssertWalkable(from, path);
    }

    /// <summary>Угол не срезается: столб на диагонали между соседями — путь идёт двумя прямыми шагами.</summary>
    [Fact]
    public void Diagonal_DoesNotCutCorners()
    {
        Wall(1, 0, 0);
        var from = Feet(0, 0, 0);
        var path = Find(from, Feet(1, 0, 1));

        Assert.Equal([Feet(0, 0, 1), Feet(1, 0, 1)], path.Points);
    }

    /// <summary>
    /// Нет пути — не ищет вечно: цель замурована, поиск останавливается на пределе клеток и ведёт к ближайшей достижимой
    /// клетке; цель дальше радиуса — путь не уходит за радиус.
    /// </summary>
    [Fact]
    public void NoPath_StopsAtNodeLimit_AndRadius()
    {
        for (var x = 9; x <= 11; x++)
        for (var z = -1; z <= 1; z++)
        for (var y = 0; y < 3; y++)
        {
            if (x != 10 || z != 0)
                Set(x, y, z, _stone);
        }
        var from = Feet(0, 0, 0);
        var walled = Find(from, Feet(10, 0, 0), maxNodes: 300);
        Assert.False(walled.Complete);
        Assert.Equal(300, walled.Expanded);
        Assert.True(Vector3d.Distance(walled.Points[^1], Feet(10, 0, 0)) < 2.5, $"кончился в {walled.Points[^1]}");

        var farStart = Feet(-30, 0, -30);
        var far = Find(farStart, Feet(30, 0, 30), maxNodes: 100_000);
        Assert.False(far.Complete);
        AssertWalkable(farStart, far); // у края радиуса путь не «перескакивает» в чужие клетки
        Assert.Equal(Feet(-30 + PathFinder.Radius, 0, -30 + PathFinder.Radius), far.Points[^1]); // угол радиуса, ближний к цели

        // Прямо на восток: путь кончается на краю радиуса, а не «продолжается» в клетках на другой стороне.
        var east = Feet(-20, 0, 0);
        var eastPath = Find(east, Feet(30, 0, 0), maxNodes: 100_000);
        Assert.False(eastPath.Complete);
        AssertWalkable(east, eastPath);
        Assert.Equal(Feet(-20 + PathFinder.Radius, 0, 0), eastPath.Points[^1]);
        Assert.All(far.Points, p => Assert.True(p.X < -30 + PathFinder.Radius + 1 && p.Z < -30 + PathFinder.Radius + 1, $"{p} за радиусом"));
    }

    /// <summary>Незагруженный мир непроходим: путь к цели в незагруженном чанке кончается у края загруженного.</summary>
    [Fact]
    public void UnloadedChunks_AreNotWalked()
    {
        var from = Feet(28, 0, 0);
        var path = Find(from, Feet(40, 0, 0));

        Assert.False(path.Complete);
        Assert.All(path.Points, p => Assert.True(p.X < 32));
    }
}
