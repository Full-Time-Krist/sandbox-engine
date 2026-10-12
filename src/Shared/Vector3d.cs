using System.Numerics;

namespace Shared;

/// <summary>
/// Позиция в мире с точностью double. У float около 7 значащих цифр: на миллионе блоков от центра
/// соседние значения отстоят на 1/16 блока, и движение, луч выбора блока и физика начинают «ступенить».
/// У double такой шаг появляется только на 10¹⁴ блоков.
/// </summary>
/// <remarks>
/// В double хранятся только абсолютные позиции (игрок, камера, начало луча). Направления, смещения
/// и вершины мешей — маленькие числа, им хватает float (<see cref="Vector3"/>), и видеокарта работает с ним.
/// </remarks>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d Zero => default;

    public Vector3d(Vector3 v) : this(v.X, v.Y, v.Z) { }

    /// <summary>float → double без потерь, поэтому неявно. Обратно — только явно, через <see cref="ToVector3"/>.</summary>
    public static implicit operator Vector3d(Vector3 v) => new(v);

    public double LengthSquared() => X * X + Y * Y + Z * Z;

    public double Length() => Math.Sqrt(LengthSquared());

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public static double Distance(Vector3d a, Vector3d b) => (a - b).Length();

    public static Vector3d Lerp(Vector3d a, Vector3d b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>
    /// Переводит в float. Только для маленьких величин — смещений относительно камеры или другой близкой точки:
    /// абсолютная позиция вдали от центра потеряет точность.
    /// </summary>
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    /// <summary>Сдвиг на смещение во float (направление × расстояние).</summary>
    public static Vector3d operator +(Vector3d a, Vector3 offset) => new(a.X + offset.X, a.Y + offset.Y, a.Z + offset.Z);
    public static Vector3d operator -(Vector3d a, Vector3 offset) => new(a.X - offset.X, a.Y - offset.Y, a.Z - offset.Z);

    public override string ToString() => $"<{X:F2}; {Y:F2}; {Z:F2}>";
}
