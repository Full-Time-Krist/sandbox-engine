using Shared.Voxels;

namespace Shared.Physics;

/// <summary>
/// Прямоугольный короб, выровненный по осям (axis-aligned bounding box). Координаты в double:
/// короб тела игрока — абсолютная позиция в мире, на миллионе блоков float дал бы ступеньки.
/// </summary>
/// <remarks>
/// Все сравнения — с допуском <see cref="Epsilon"/>. Тело, остановленное о стену, стоит вплотную к ней,
/// но после сложения и вычитания double может оказаться на 10⁻¹⁰ внутри. Без допуска такой короб
/// «пересекал» бы стену, и на следующем тике она перестала бы его останавливать.
/// </remarks>
public readonly record struct Aabb(Vector3d Min, Vector3d Max)
{
    /// <summary>
    /// Насколько короба могут заходить друг в друга, не считаясь пересекающимися. Шаг double на 10⁹ блоков
    /// (предел координат сервера) — около 10⁻⁷, допуск в несколько раз больше; на глаз 10⁻⁶ блока не видно.
    /// </summary>
    public const double Epsilon = 1e-6;

    /// <summary>Короб блока: куб от (X, Y, Z) до (X + 1, Y + 1, Z + 1).</summary>
    public static Aabb OfBlock(BlockPos block) =>
        new(new Vector3d(block.X, block.Y, block.Z), new Vector3d(block.X + 1, block.Y + 1, block.Z + 1));

    public Aabb Offset(Vector3d delta) => new(Min + delta, Max + delta);

    /// <summary>
    /// Короб, растянутый на перемещение <paramref name="delta"/>: всё пространство, которое тело заденет
    /// по пути. Блоки внутри него — все, о которые можно удариться за это перемещение.
    /// </summary>
    public Aabb Stretch(Vector3d delta) => new(
        new Vector3d(Math.Min(Min.X, Min.X + delta.X), Math.Min(Min.Y, Min.Y + delta.Y), Math.Min(Min.Z, Min.Z + delta.Z)),
        new Vector3d(Math.Max(Max.X, Max.X + delta.X), Math.Max(Max.Y, Max.Y + delta.Y), Math.Max(Max.Z, Max.Z + delta.Z)));

    /// <summary>Пересекаются ли короба больше чем на <see cref="Epsilon"/>. Касание гранями — не пересечение.</summary>
    public bool Intersects(Aabb other) =>
        OverlapsX(other) && OverlapsY(other) && OverlapsZ(other);

    /// <summary>
    /// Насколько этот короб может сдвинуться по X на <paramref name="dx"/>, не войдя в <paramref name="obstacle"/>.
    /// Препятствие учитывается, только если оно на пути: перекрывается по двум другим осям и лежит впереди.
    /// Препятствие, в которое короб уже вошёл, не учитывается: иначе застрявшее тело не смогло бы выбраться.
    /// </summary>
    public double ClipX(Aabb obstacle, double dx)
    {
        if (!OverlapsY(obstacle) || !OverlapsZ(obstacle))
            return dx;
        if (dx > 0 && Max.X <= obstacle.Min.X + Epsilon)
            return Math.Min(dx, obstacle.Min.X - Max.X);
        if (dx < 0 && Min.X >= obstacle.Max.X - Epsilon)
            return Math.Max(dx, obstacle.Max.X - Min.X);
        return dx;
    }

    /// <inheritdoc cref="ClipX"/>
    public double ClipY(Aabb obstacle, double dy)
    {
        if (!OverlapsX(obstacle) || !OverlapsZ(obstacle))
            return dy;
        if (dy > 0 && Max.Y <= obstacle.Min.Y + Epsilon)
            return Math.Min(dy, obstacle.Min.Y - Max.Y);
        if (dy < 0 && Min.Y >= obstacle.Max.Y - Epsilon)
            return Math.Max(dy, obstacle.Max.Y - Min.Y);
        return dy;
    }

    /// <inheritdoc cref="ClipX"/>
    public double ClipZ(Aabb obstacle, double dz)
    {
        if (!OverlapsX(obstacle) || !OverlapsY(obstacle))
            return dz;
        if (dz > 0 && Max.Z <= obstacle.Min.Z + Epsilon)
            return Math.Min(dz, obstacle.Min.Z - Max.Z);
        if (dz < 0 && Min.Z >= obstacle.Max.Z - Epsilon)
            return Math.Max(dz, obstacle.Max.Z - Min.Z);
        return dz;
    }

    private bool OverlapsX(Aabb other) => Max.X > other.Min.X + Epsilon && Min.X < other.Max.X - Epsilon;
    private bool OverlapsY(Aabb other) => Max.Y > other.Min.Y + Epsilon && Min.Y < other.Max.Y - Epsilon;
    private bool OverlapsZ(Aabb other) => Max.Z > other.Min.Z + Epsilon && Min.Z < other.Max.Z - Epsilon;

    public override string ToString() => $"[{Min} — {Max}]";
}
