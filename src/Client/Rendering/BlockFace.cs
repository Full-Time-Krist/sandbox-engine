using System.Numerics;

namespace Client.Rendering;

/// <summary>Одна из шести граней куба блока: направление к соседу, яркость и углы квадрата.</summary>
internal readonly struct BlockFace(int dx, int dy, int dz, float brightness, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
{
    /// <summary>Смещение к соседнему блоку, который может закрыть эту грань. Совпадает с нормалью грани.</summary>
    public readonly int Dx = dx, Dy = dy, Dz = dz;

    /// <summary>Постоянная яркость грани вместо освещения: так видно объём.</summary>
    public readonly float Brightness = brightness;

    /// <summary>Углы единичного куба [0; 1]³ против часовой стрелки, если смотреть снаружи.</summary>
    public readonly Vector3 A = a, B = b, C = c, D = d;

    public Vector3 Normal => new(Dx, Dy, Dz);

    public static readonly BlockFace[] All =
    [
        new( 1,  0,  0, 0.6f, new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)), // +X восток
        new(-1,  0,  0, 0.6f, new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0)), // −X запад
        new( 0,  1,  0, 1.0f, new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0)), // +Y верх
        new( 0, -1,  0, 0.5f, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)), // −Y низ
        new( 0,  0,  1, 0.8f, new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)), // +Z юг
        new( 0,  0, -1, 0.8f, new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)), // −Z север
    ];
}
