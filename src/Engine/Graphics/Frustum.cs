using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Пирамида видимости камеры: шесть плоскостей, нормали которых смотрят внутрь.
/// Нужна, чтобы не рисовать то, что заведомо не попадёт на экран (frustum culling).
/// </summary>
/// <remarks>
/// <para>
/// Плоскости берутся прямо из матрицы ViewProjection (метод Gribb–Hartmann). Матрица переводит точку
/// в координаты отсечения (x, y, z, w), и точка видна, если −w ≤ x ≤ w, −w ≤ y ≤ w и 0 ≤ z ≤ w
/// (глубина от 0 до 1, см. glClipControl). Каждое неравенство — плоскость: например, x + w ≥ 0.
/// В System.Numerics вектор умножается на матрицу слева (v * M), поэтому x = v · (столбец 1),
/// w = v · (столбец 4), и коэффициенты плоскости — суммы и разности столбцов.
/// </para>
/// </remarks>
public readonly struct Frustum
{
    private readonly Plane[] _planes;

    private Frustum(Plane[] planes) => _planes = planes;

    public static Frustum FromMatrix(Matrix4x4 m)
    {
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var w = new Vector4(m.M14, m.M24, m.M34, m.M44);

        return new Frustum([
            ToPlane(w + x), // левая:   x ≥ −w
            ToPlane(w - x), // правая:  x ≤ w
            ToPlane(w + y), // нижняя:  y ≥ −w
            ToPlane(w - y), // верхняя: y ≤ w
            ToPlane(z),     // ближняя: z ≥ 0
            ToPlane(w - z), // дальняя: z ≤ w
        ]);
    }

    private static Plane ToPlane(Vector4 v) => Plane.Normalize(new Plane(v.X, v.Y, v.Z, v.W));

    /// <summary>
    /// Пересекает ли пирамиду параллелепипед, выровненный по осям. Может вернуть true для коробки чуть снаружи
    /// у углов пирамиды (лишний чанк нарисуется), но никогда не вернёт false для видимой: картинка не пострадает.
    /// </summary>
    public bool Intersects(Vector3 min, Vector3 max)
    {
        foreach (var plane in _planes)
        {
            // Вершина коробки, дальше всех продвинутая вдоль нормали внутрь. Если даже она снаружи — снаружи вся коробка.
            var farthest = new Vector3(
                plane.Normal.X >= 0 ? max.X : min.X,
                plane.Normal.Y >= 0 ? max.Y : min.Y,
                plane.Normal.Z >= 0 ? max.Z : min.Z);

            if (Plane.DotCoordinate(plane, farthest) < 0)
                return false;
        }
        return true;
    }
}
