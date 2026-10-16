using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Ui;

/// <summary>Цвет интерфейса: RGBA по 8 бит.</summary>
public readonly record struct UiColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly UiColor White = new(255, 255, 255);
    public static readonly UiColor Black = new(0, 0, 0);

    /// <summary>Упакованный вид для вершины: R — младший байт, то есть в памяти R, G, B, A.</summary>
    public uint Packed => R | (uint)G << 8 | (uint)B << 16 | (uint)A << 24;

    /// <summary>Тот же цвет с другой непрозрачностью (0 — прозрачный, 1 — как есть).</summary>
    public UiColor WithAlpha(float alpha) => this with { A = (byte)Math.Clamp(MathF.Round(A * alpha), 0, 255) };

    /// <summary>Затемнение или осветление: каждый канал цвета умножается на <paramref name="brightness"/>.</summary>
    public UiColor Shade(float brightness)
    {
        return new UiColor(Scale(R), Scale(G), Scale(B), A);

        byte Scale(byte channel) => (byte)Math.Clamp(MathF.Round(channel * brightness), 0, 255);
    }
}

/// <summary>
/// Вершина интерфейса. Обычная структура с полями, а не позиционный record: смещения полей
/// (<see cref="Marshal.OffsetOf"/>) нужны для описания атрибутов.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct UiVertex(Vector2 position, Vector2 uv, float layer, uint color)
{
    /// <summary>Пиксели экрана: (0, 0) — левый верхний угол, Y растёт вниз.</summary>
    public Vector2 Position = position;

    public Vector2 Uv = uv;

    /// <summary>Слой массива текстур (иконки блоков) или <see cref="UiCanvas.AtlasLayer"/> — атлас интерфейса.</summary>
    public float Layer = layer;

    /// <summary>Цвет, на который умножается текстура (<see cref="UiColor.Packed"/>).</summary>
    public uint Color = color;
}
