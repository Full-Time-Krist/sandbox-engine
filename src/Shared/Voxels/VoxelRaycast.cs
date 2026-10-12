using System.Numerics;

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
public static class VoxelRaycast
{
    /// <param name="origin">Начало луча — абсолютная позиция, поэтому в double (вдали от центра float теряет точность).</param>
    /// <param name="direction">Направление; длина не важна.</param>
    public static BlockHit? Cast(ChunkMap world, Vector3d origin, Vector3 direction, float maxDistance)
    {
        if (direction == Vector3.Zero || !float.IsFinite(direction.LengthSquared()))
            return null;

        direction = Vector3.Normalize(direction);
        var current = BlockPos.Containing(origin);

        if (!world.GetBlock(current).IsAir)
            return new BlockHit(current, default, 0f);

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

            if (!world.GetBlock(current).IsAir)
                return new BlockHit(current, normal, (float)distance);
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
