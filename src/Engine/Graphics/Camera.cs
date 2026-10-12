using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Перспективная камера с углами рыскания (yaw) и тангажа (pitch).
/// Система координат правая: X вправо, Y вверх, при нулевых углах взгляд направлен вдоль −Z.
/// </summary>
public sealed class Camera
{
    // Чуть меньше 90°: при взгляде строго вверх forward совпадает с up и CreateLookAt вырождается.
    private const float MaxPitch = 89f * MathF.PI / 180f;

    private float _yaw;
    private float _pitch;

    public Vector3 Position { get; set; }

    /// <summary>Поворот вокруг вертикальной оси в радианах. Положительный поворачивает вправо.</summary>
    public float Yaw
    {
        get => _yaw;
        // Держим угол в пределах [−π; π], чтобы он не терял точность после долгого кручения.
        set => _yaw = MathF.IEEERemainder(value, MathF.Tau);
    }

    /// <summary>Наклон в радианах. Положительный наклоняет взгляд вверх.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    /// <summary>Вертикальный угол обзора в радианах.</summary>
    public float FieldOfView { get; set; } = 70f * MathF.PI / 180f;
    public float AspectRatio { get; set; } = 16f / 9f;
    public float NearPlane { get; set; } = 0.05f;
    public float FarPlane { get; set; } = 1000f;

    public Vector3 Forward
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(_yaw);
            var (sinPitch, cosPitch) = MathF.SinCos(_pitch);
            return new Vector3(sinYaw * cosPitch, sinPitch, -cosYaw * cosPitch);
        }
    }

    /// <summary>Направление «вперёд» в горизонтальной плоскости, без наклона. Нужно для ходьбы.</summary>
    public Vector3 FlatForward
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(_yaw);
            return new Vector3(sinYaw, 0f, -cosYaw);
        }
    }

    public Vector3 Right
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(_yaw);
            return new Vector3(cosYaw, 0f, sinYaw);
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    // Глубина в диапазоне [0; 1]: движок включает glClipControl(ZERO_TO_ONE), см. Application.
    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// View × Projection в порядке System.Numerics (вектор-строка слева).
    /// В шейдере та же матрица умножается как <c>u_ViewProjection * vec4(pos, 1)</c>.
    /// </summary>
    public Matrix4x4 ViewProjection => View * Projection;
}
