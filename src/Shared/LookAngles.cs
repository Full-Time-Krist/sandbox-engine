using System.Numerics;

namespace Shared;

/// <summary>Направление взгляда игрока: углы камеры в радианах, как у <c>Engine.Graphics.Camera</c>.</summary>
/// <param name="Yaw">Поворот вокруг вертикали: положительный — вправо, 0 — взгляд вдоль −Z.</param>
/// <param name="Pitch">Наклон: положительный — вверх, от −π/2 до π/2.</param>
public readonly record struct LookAngles(float Yaw, float Pitch)
{
    public bool IsFinite => float.IsFinite(Yaw) && float.IsFinite(Pitch);

    /// <summary>Единичный вектор взгляда — та же формула, что у камеры клиента.</summary>
    public Vector3 Forward
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(Yaw);
            var (sinPitch, cosPitch) = MathF.SinCos(Pitch);
            return new Vector3(sinYaw * cosPitch, sinPitch, -cosYaw * cosPitch);
        }
    }

    /// <summary>
    /// Yaw в пределах [−π; π], Pitch — [−π/2; π/2]. Углы приходят от клиента и с диска, и конечные числа
    /// могут оказаться любыми; NaN сюда не передавать (<see cref="IsFinite"/>).
    /// </summary>
    public LookAngles Normalized() =>
        new(MathF.IEEERemainder(Yaw, MathF.Tau), Math.Clamp(Pitch, -MathF.PI / 2, MathF.PI / 2));
}
