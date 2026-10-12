using System.Numerics;
using System.Runtime.InteropServices;

namespace Client.Rendering;

/// <summary>
/// Вершина меша чанка, 28 байт. Пока без упаковки: сначала правильность.
/// Сжатие в 8 байт (позиции и UV — маленькие целые числа) — отдельный шаг оптимизации.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ChunkVertex(Vector3 position, Vector2 uv, float layer, float shade)
{
    public readonly Vector3 Position = position;
    public readonly Vector2 Uv = uv;
    /// <summary>Слой массива текстур. float точно хранит целые до 2²⁴, этого хватит с запасом.</summary>
    public readonly float Layer = layer;
    public readonly float Shade = shade;
}
