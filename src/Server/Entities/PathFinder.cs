using System.Collections.Immutable;
using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>
/// Путь моба: точки ног в центрах клеток по порядку, без клетки, где моб стоял. <see cref="Complete"/> — дошёл до цели;
/// иначе путь ведёт к ближайшей к цели клетке, до которой удалось дойти (цель в воздухе, за стеной, дальше радиуса).
/// </summary>
/// <param name="Expanded">Сколько клеток раскрыл поиск — его цена для бюджета тика.</param>
internal sealed record MobPath(ImmutableArray<Vector3d> Points, bool Complete, int Expanded);

/// <summary>
/// Поиск пути A* по клеткам мира — шагами, которые тело моба осилит физикой ходьбы (<see cref="PlayerPhysics.Step(PlayerState, PlayerInput, Walker, ChunkMap, BlockRegistry)"/>):
/// в соседнюю клетку (по диагонали — только по ровному и если оба соседних прямых шага возможны, чтобы не срезать угол),
/// вверх не выше уступа <see cref="PlayerPhysics.StepHeight"/>, вниз не глубже <see cref="MaxDrop"/>. Клетка годится,
/// если в ней есть опора под ноги и тело там помещается.
/// </summary>
/// <remarks>
/// Поиск ограничен: не дальше <see cref="Radius"/> от начала по X и Z, не дальше <see cref="VerticalRadius"/> по высоте и
/// не больше заданного числа раскрытых клеток. Нет пути — поиск кончается на пределе и отдаёт путь к ближайшей достигнутой
/// клетке, а не ищет вечно. Незагруженный мир — непроходим: моб там всё равно стоял бы на месте.
/// <para>
/// Поиск — горячее место тика (замер шага 9.3 — в разделе 17 DESIGN.md). Раз он не уходит за радиус, клетки лежат в
/// массивах вокруг начала (индекс, а не хеш), а «очищаются» номером поиска: значение верно, только если его штамп равен
/// номеру текущего поиска. Где можно стоять, запоминается (каждую клетку спрашивают до восьми соседей); тело проверяется
/// по клеткам с быстрым ответом для воздуха и полного куба — как <see cref="PlayerPhysics.Collides(Walker, Vector3d, ChunkMap, BlockRegistry)"/>
/// (короба формы твёрдых блоков, тот же допуск, незагруженное — пусто); последний чанк держится под рукой.
/// </para>
/// </remarks>
internal sealed class PathFinder(ChunkMap chunks, BlockRegistry blocks)
{
    /// <summary>Сколько клеток раскрывает один поиск, не больше (раздел 17 DESIGN.md).</summary>
    public const int MaxNodes = 1000;

    /// <summary>Насколько далеко от начала по X и Z может уйти путь, блоков.</summary>
    public const int Radius = 24;

    /// <summary>Насколько выше или ниже начала может уйти путь, блоков.</summary>
    public const int VerticalRadius = 31;

    /// <summary>С какой высоты моб готов спрыгнуть, блоков: падение с 3 ещё без урона.</summary>
    public const int MaxDrop = 3;

    private const double Diagonal = 1.41421356;
    private const double ClimbCost = 0.5;
    private const int Side = 2 * Radius + 1;
    private const int Height = 2 * VerticalRadius + 2;
    private const int Cells = Side * Side * Height;

    private static readonly (int Dx, int Dz)[] Straight = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    // Клетки вокруг начала поиска. Штампы — номер поиска, в котором значение записано.
    private readonly int[] _nodeStamp = new int[Cells];
    private readonly int[] _floorStamp = new int[Cells];
    private readonly double[] _cost = new double[Cells];
    private readonly double[] _nodeFloor = new double[Cells];
    private readonly int[] _parent = new int[Cells];
    private readonly bool[] _closed = new bool[Cells];

    /// <summary>Где можно стоять: высота ног или NaN — нельзя. Действует один поиск (мир между тиками меняется).</summary>
    private readonly double[] _floor = new double[Cells];
    private readonly PriorityQueue<int, double> _open = new();
    private int _search;
    private Walker _walker;
    private BlockPos _start;
    private ChunkPos _lastChunkPos;
    private Chunk? _lastChunk;
    private bool _hasLastChunk;

    /// <summary>Один поиск на свежем экземпляре — для тестов и редких вызовов.</summary>
    public static MobPath? Find(Walker walker, Vector3d from, Vector3d goal, ChunkMap chunks, BlockRegistry blocks, int maxNodes = MaxNodes) =>
        new PathFinder(chunks, blocks).Search(walker, from, goal, maxNodes);

    /// <summary>Можно ли стоять в клетке (см. <see cref="Search"/>); <paramref name="floor"/> — высота ног.</summary>
    internal static bool Standable(Walker walker, BlockPos cell, ChunkMap chunks, BlockRegistry blocks, out double floor)
    {
        var finder = new PathFinder(chunks, blocks) { _walker = walker };
        floor = finder.ComputeFloor(cell);
        return !double.IsNaN(floor);
    }

    /// <param name="from">Ноги моба сейчас.</param>
    /// <param name="goal">Куда нужно: точка, не обязательно на земле (игрок в прыжке, случайная точка блуждания).</param>
    /// <param name="maxNodes">Предел раскрытых клеток.</param>
    /// <returns>Путь или null, если из начала не сделать ни шага ближе к цели.</returns>
    public MobPath? Search(Walker walker, Vector3d from, Vector3d goal, int maxNodes = MaxNodes)
    {
        _walker = walker;
        _open.Clear();
        _hasLastChunk = false;
        if (++_search == int.MaxValue)
        {
            // Номера кончились (через 2 миллиарда поисков) — штампы обнуляются честно.
            Array.Clear(_nodeStamp);
            Array.Clear(_floorStamp);
            _search = 1;
        }

        var start = _start = BlockPos.Containing(from + new Vector3d(0, 1e-3, 0));
        var target = BlockPos.Containing(goal + new Vector3d(0, 1e-3, 0));
        var startIndex = IndexOf(start);
        SetNode(startIndex, 0, from.Y, startIndex);
        _open.Enqueue(startIndex, Heuristic(start, from.Y, target, goal.Y));

        var best = startIndex;
        var bestDistance = Heuristic(start, from.Y, target, goal.Y);
        var expanded = 0;
        var complete = false;
        Span<double> straightFloors = stackalloc double[4];

        while (expanded < maxNodes && _open.TryDequeue(out var index, out _))
        {
            if (_closed[index])
                continue;
            _closed[index] = true;
            expanded++;
            var cell = CellOf(index);
            var (floor, cost) = (_nodeFloor[index], _cost[index]);
            if (cell.X == target.X && cell.Z == target.Z && Math.Abs(floor - goal.Y) <= 1)
            {
                best = index;
                complete = true;
                break;
            }
            var distance = Heuristic(cell, floor, target, goal.Y);
            if (distance < bestDistance)
                (best, bestDistance) = (index, distance);

            for (var i = 0; i < Straight.Length; i++)
            {
                var (dx, dz) = Straight[i];
                straightFloors[i] = double.NaN;
                if (TryStep(cell, floor, dx, dz, out var next, out var nextFloor))
                {
                    straightFloors[i] = nextFloor;
                    Relax(index, next, nextFloor, cost + 1 + (nextFloor > floor ? ClimbCost : 0), target, goal.Y);
                }
            }
            // Диагональ — только по ровному: оба прямых соседа на той же высоте, и сама клетка тоже.
            for (var i = 0; i < 2; i++)
            for (var j = 2; j < 4; j++)
            {
                if (straightFloors[i] != floor || straightFloors[j] != floor)
                    continue;
                if (TryStep(cell, floor, Straight[i].Dx, Straight[j].Dz, out var next, out var nextFloor) && nextFloor == floor)
                    Relax(index, next, nextFloor, cost + Diagonal, target, goal.Y);
            }
        }

        if (best == startIndex)
            return null;
        var points = new List<Vector3d>();
        for (var index = best; index != startIndex; index = _parent[index])
        {
            var cell = CellOf(index);
            points.Add(new Vector3d(cell.X + 0.5, _nodeFloor[index], cell.Z + 0.5));
        }
        points.Reverse();
        return new MobPath([.. points], complete, expanded);
    }

    private void SetNode(int index, double cost, double floor, int parent)
    {
        if (_nodeStamp[index] != _search)
        {
            _nodeStamp[index] = _search;
            _closed[index] = false;
        }
        _cost[index] = cost;
        _nodeFloor[index] = floor;
        _parent[index] = parent;
    }

    private void Relax(int from, int to, double floor, double cost, BlockPos target, double goalY)
    {
        if (_nodeStamp[to] == _search && (_closed[to] || _cost[to] <= cost))
            return;
        SetNode(to, cost, floor, from);
        _open.Enqueue(to, cost + Heuristic(CellOf(to), floor, target, goalY));
    }

    /// <summary>
    /// Шаг из клетки <paramref name="cell"/> (ноги на высоте <paramref name="floor"/>) в соседний столбец: сначала вверх
    /// на уступ (тело должно подняться на месте), иначе — первая опора вниз (тело должно пройти над краем на своей высоте).
    /// </summary>
    private bool TryStep(BlockPos cell, double floor, int dx, int dz, out int next, out double nextFloor)
    {
        var (x, z) = (cell.X + dx, cell.Z + dz);
        for (var dy = 1; dy >= -MaxDrop - 1; dy--)
        {
            // За радиусом клеток нет (IndexOf) — путь туда не уходит.
            var candidate = new BlockPos(x, cell.Y + dy, z);
            next = IndexOf(candidate);
            if (next < 0)
                continue;
            nextFloor = FloorOf(next, candidate);
            if (double.IsNaN(nextFloor) || nextFloor > floor + PlayerPhysics.StepHeight + 1e-6 || nextFloor < floor - MaxDrop - 1e-6)
                continue;
            // Поднялся на месте, потом шагнул — или шагнул над краем на своей высоте, потом упал. По ровному проход —
            // то самое место, где тело уже проверено (FloorOf).
            if (nextFloor == floor || (nextFloor > floor ? BodyFits(cell.X + 0.5, nextFloor, cell.Z + 0.5) : BodyFits(x + 0.5, floor, z + 0.5)))
                return true;
        }
        (next, nextFloor) = (-1, 0);
        return false;
    }

    /// <summary>Клетка вокруг начала — в индекс массивов; −1 — за радиусом.</summary>
    private int IndexOf(BlockPos cell)
    {
        var (x, y, z) = (cell.X - _start.X + Radius, cell.Y - _start.Y + VerticalRadius, cell.Z - _start.Z + Radius);
        if ((uint)x >= Side || (uint)z >= Side || (uint)y >= Height)
            return -1;
        return (y * Side + z) * Side + x;
    }

    private BlockPos CellOf(int index)
    {
        var x = index % Side;
        var z = index / Side % Side;
        var y = index / (Side * Side);
        return new BlockPos(x - Radius + _start.X, y - VerticalRadius + _start.Y, z - Radius + _start.Z);
    }

    private double FloorOf(int index, BlockPos cell)
    {
        if (_floorStamp[index] == _search)
            return _floor[index];
        _floorStamp[index] = _search;
        return _floor[index] = ComputeFloor(cell);
    }

    /// <summary>
    /// Высота ног в клетке или NaN — стоять нельзя. Опора — верх неполного твёрдого блока в самой клетке (плита) или
    /// полный верх блока под ней; тело помещается. В незагруженном опоры нет: там воздух.
    /// </summary>
    private double ComputeFloor(BlockPos cell)
    {
        var block = BlockAt(cell);
        double floor;
        if (blocks.IsSolid(block))
        {
            var top = blocks.ShapeOf(block).Bounds.Max.Y;
            if (top >= 1)
                return double.NaN;
            floor = cell.Y + top;
        }
        else
        {
            var support = BlockAt(cell with { Y = cell.Y - 1 });
            if (!blocks.IsSolid(support) || blocks.ShapeOf(support).Bounds.Max.Y < 1)
                return double.NaN;
            floor = cell.Y;
        }
        return BodyFits(cell.X + 0.5, floor, cell.Z + 0.5) ? floor : double.NaN;
    }

    /// <summary>
    /// Помещается ли тело ногами в точке: ни один короб формы твёрдого блока его не пересекает — как
    /// <see cref="PlayerPhysics.Collides(Walker, Vector3d, ChunkMap, BlockRegistry)"/> (те же клетки, тот же допуск), только
    /// без перебора коробов у воздуха и полного куба. Незагруженное — пусто.
    /// </summary>
    private bool BodyFits(double x, double feet, double z)
    {
        var body = _walker.BodyAt(new Vector3d(x, feet, z));
        var (min, max) = (BlockPos.Containing(body.Min), BlockPos.Containing(body.Max));
        for (var by = min.Y; by <= max.Y; by++)
        for (var bx = min.X; bx <= max.X; bx++)
        for (var bz = min.Z; bz <= max.Z; bz++)
        {
            var cell = new BlockPos(bx, by, bz);
            var block = BlockAt(cell);
            if (!blocks.IsSolid(block))
                continue;
            var shape = blocks.ShapeOf(block);
            if (shape.IsFullCube)
            {
                if (body.Intersects(Aabb.OfBlock(cell)))
                    return false;
                continue;
            }
            foreach (var box in shape.Boxes)
            {
                if (body.Intersects(Aabb.Of(cell, box)))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Блок в клетке; в незагруженном чанке — воздух, как у <see cref="ChunkMap.GetBlock"/>: там нет опоры, поэтому
    /// незагруженное непроходимо само собой. Последний чанк держится под рукой.
    /// </summary>
    private BlockId BlockAt(BlockPos cell)
    {
        var chunkPos = cell.Chunk;
        if (!_hasLastChunk || chunkPos != _lastChunkPos)
        {
            _lastChunk = chunks.Get(chunkPos);
            _lastChunkPos = chunkPos;
            _hasLastChunk = true;
        }
        return _lastChunk?.Get(cell.Local) ?? BlockId.Air;
    }

    /// <summary>Оценка остатка пути: по горизонтали — восьмисвязное расстояние, по высоте — половина разницы.</summary>
    private static double Heuristic(BlockPos cell, double floor, BlockPos target, double goalY)
    {
        var dx = Math.Abs(cell.X - target.X);
        var dz = Math.Abs(cell.Z - target.Z);
        return Math.Max(dx, dz) + (Diagonal - 1) * Math.Min(dx, dz) + Math.Abs(floor - goalY) * 0.5;
    }
}
