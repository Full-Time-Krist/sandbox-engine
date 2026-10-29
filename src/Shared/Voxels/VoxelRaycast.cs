using System.Numerics;
using Shared.Blocks;

namespace Shared.Voxels;

/// <summary>Результат луча: первый непустой блок на пути.</summary>
/// <param name="Block">Блок, в который попал луч.</param>
/// <param name="Normal">
/// Нормаль грани, через которую луч вошёл в блок: смещение к соседу со стороны наблюдателя.
/// <c>Block + Normal</c> — место для установки нового блока. Нулевая, если луч начался внутри блока.
/// </param>
/// <param name="Distance">Расстояние от начала луча до точки входа в блок.</param>
public readonly record struct BlockHit(BlockPos Block, BlockPos Normal, float Distance);

/// <summary>
/// Поиск блока по лучу алгоритмом DDA (Amanatides, Woo, 1987). Луч идёт от одной границы блоков
/// до следующей ближайшей, поэтому проверяет ровно те блоки, которые пересекает, и не пропускает углы.
/// </summary>
/// <remarks>
/// В блок не-куб (<see cref="BlockShape"/>: факел, полублок) луч попадает, только задев один из его коробов, — тогда
/// нормаль и расстояние берутся от грани короба; мимо коробов луч идёт дальше, к блоку за ним.
/// </remarks>
public static class VoxelRaycast
{
    /// <param name="origin">Начало луча — абсолютная позиция, поэтому в double (вдали от центра float теряет точность).</param>
    /// <param name="direction">Направление; длина не важна.</param>
    /// <param name="blocks">Откуда брать формы блоков.</param>
    public static BlockHit? Cast(ChunkMap world, BlockRegistry blocks, Vector3d origin, Vector3 direction, float maxDistance)
    {
        if (direction == Vector3.Zero || !float.IsFinite(direction.LengthSquared()))
            return null;

        direction = Vector3.Normalize(direction);
        var current = BlockPos.Containing(origin);

        // Начало в клетке блока: у куба луч уже внутри (нормали нет), у не-куба — проверяем его короба.
        if (world.GetBlock(current) is { IsAir: false } start
            && HitShape(blocks.ShapeOf(start), current, default, 0, origin, direction, maxDistance) is { } inside)
            return inside;

        // По каждой оси: шаг (±1 или 0), расстояние вдоль луча до первой границы блока (tMax)
        // и расстояние между соседними границами (tDelta).
        var (stepX, tMaxX, tDeltaX) = SetupAxis(origin.X, direction.X);
        var (stepY, tMaxY, tDeltaY) = SetupAxis(origin.Y, direction.Y);
        var (stepZ, tMaxZ, tDeltaZ) = SetupAxis(origin.Z, direction.Z);

        while (true)
        {
            BlockPos normal;
            double distance;

            // Переходим через ближайшую границу. Нормаль грани входа противоположна шагу.
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                distance = tMaxX;
                current = current with { X = current.X + stepX };
                tMaxX += tDeltaX;
                normal = new BlockPos(-stepX, 0, 0);
            }
            else if (tMaxY <= tMaxZ)
            {
                distance = tMaxY;
                current = current with { Y = current.Y + stepY };
                tMaxY += tDeltaY;
                normal = new BlockPos(0, -stepY, 0);
            }
            else
            {
                distance = tMaxZ;
                current = current with { Z = current.Z + stepZ };
                tMaxZ += tDeltaZ;
                normal = new BlockPos(0, 0, -stepZ);
            }

            if (distance > maxDistance)
                return null;

            if (world.GetBlock(current) is { IsAir: false } block
                && HitShape(blocks.ShapeOf(block), current, normal, distance, origin, direction, maxDistance) is { } hit)
                return hit;
        }
    }

    /// <summary>
    /// Попадание в блок формы <paramref name="shape"/>: у куба — в точке входа в клетку; у не-куба — в ближайший короб
    /// (null — мимо всех коробов или дальше <paramref name="maxDistance"/>).
    /// </summary>
    private static BlockHit? HitShape(BlockShape shape, BlockPos cell, BlockPos cellNormal, double cellDistance,
        Vector3d origin, Vector3 direction, float maxDistance)
    {
        if (shape.IsFullCube)
            return new BlockHit(cell, cellNormal, (float)cellDistance);

        BlockHit? best = null;
        foreach (var box in shape.Boxes)
        {
            var min = new Vector3d(cell.X + box.Min.X, cell.Y + box.Min.Y, cell.Z + box.Min.Z);
            var max = new Vector3d(cell.X + box.Max.X, cell.Y + box.Max.Y, cell.Z + box.Max.Z);
            if (HitBox(origin, direction, min, max) is { } hit && hit.Distance <= maxDistance
                && (best is null || hit.Distance < best.Value.Distance))
                best = new BlockHit(cell, hit.Normal, (float)hit.Distance);
        }
        return best;
    }

    /// <summary>
    /// Расстояние по лучу до короба <paramref name="box"/> (тела сущности): до точки входа, 0 — луч начался внутри,
    /// null — мимо. Тот же расчёт, что у коробов формы блоков.
    /// </summary>
    public static double? Distance(Physics.Aabb box, Vector3d origin, Vector3 direction)
    {
        if (direction == Vector3.Zero || !float.IsFinite(direction.LengthSquared()))
            return null;
        return HitBox(origin, Vector3.Normalize(direction), box.Min, box.Max)?.Distance;
    }

    /// <summary>
    /// Пересечение луча с коробом (метод плит): расстояние до входа и нормаль грани входа; луч из короба — расстояние 0
    /// и нулевая нормаль; null — мимо. Разности считаются в double: короб и начало луча — абсолютные позиции.
    /// </summary>
    private static (double Distance, BlockPos Normal)? HitBox(Vector3d origin, Vector3 direction, Vector3d min, Vector3d max)
    {
        double enter = 0, exit = double.PositiveInfinity;
        BlockPos normal = default;
        if (!Axis(origin.X, direction.X, min.X, max.X, new BlockPos(1, 0, 0), ref enter, ref exit, ref normal)
            || !Axis(origin.Y, direction.Y, min.Y, max.Y, new BlockPos(0, 1, 0), ref enter, ref exit, ref normal)
            || !Axis(origin.Z, direction.Z, min.Z, max.Z, new BlockPos(0, 0, 1), ref enter, ref exit, ref normal))
            return null;
        return (enter, normal);

        static bool Axis(double o, float d, double lo, double hi, BlockPos unit, ref double enter, ref double exit, ref BlockPos normal)
        {
            if (d == 0f)
                return o >= lo && o <= hi;
            var near = ((d > 0 ? lo : hi) - o) / d;
            var far = ((d > 0 ? hi : lo) - o) / d;
            if (near > enter)
            {
                enter = near;
                // Входим через грань, обращённую к началу луча: при движении в +ось это грань «минус».
                normal = d > 0 ? new BlockPos(-unit.X, -unit.Y, -unit.Z) : unit;
            }
            exit = Math.Min(exit, far);
            return enter <= exit;
        }
    }

    private static (int Step, double TMax, double TDelta) SetupAxis(double origin, float direction)
    {
        // Луч параллелен этой оси: её границы он никогда не пересечёт.
        if (direction == 0f)
            return (0, double.PositiveInfinity, double.PositiveInfinity);

        // Расстояние до границы считается от дробной части начала в double: во float на миллионе блоков
        // дробная часть была бы кратна 1/16, и луч выбирал бы соседний блок у края.
        var cell = Math.Floor(origin);
        var tDelta = 1.0 / Math.Abs(direction);
        var tMax = direction > 0
            ? (cell + 1 - origin) * tDelta
            : (origin - cell) * tDelta;

        return (direction > 0 ? 1 : -1, tMax, tDelta);
    }
}
