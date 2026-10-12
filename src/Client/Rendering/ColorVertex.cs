using System.Numerics;
using System.Runtime.InteropServices;

namespace Client.Rendering;

// Обычные поля, а не record struct: Marshal.OffsetOf ищет поле по имени,
// а у позиционного record поля скрыты за авто-свойствами.
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ColorVertex(Vector3 position, Vector3 color)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Color = color;
}
