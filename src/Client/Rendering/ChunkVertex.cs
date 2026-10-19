using System.Numerics;
using System.Runtime.InteropServices;

namespace Client.Rendering;

/// <summary>
/// Вершина меша чанка, 36 байт. Пока без упаковки: сначала правильность.
/// Сжатие (позиции и UV — маленькие целые числа, свет — 4 бита) — отдельный шаг оптимизации.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ChunkVertex(Vector3 position, Vector2 uv, float layer, float shade, float sky, float blockLight)
{
    public readonly Vector3 Position = position;
    public readonly Vector2 Uv = uv;
    /// <summary>Слой массива текстур. float точно хранит целые до 2²⁴, этого хватит с запасом.</summary>
    public readonly float Layer = layer;
    /// <summary>Постоянная яркость грани (<see cref="BlockFace.Brightness"/>) × затенение угла.</summary>
    public readonly float Shade = shade;
    /// <summary>Небесный свет угла, 0–15 (среднее по соседним блокам, поэтому дробный). Яркость неба применяет шейдер.</summary>
    public readonly float Sky = sky;
    /// <summary>Блочный свет угла, 0–15.</summary>
    public readonly float BlockLight = blockLight;
}
