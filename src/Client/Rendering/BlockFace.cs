using System.Numerics;

namespace Client.Rendering;

internal enum FaceKind
{
    Top,
    Side,
    Bottom,
}

/// <summary>Одна из шести граней куба блока: направление к соседу, яркость, углы квадрата и их UV.</summary>
internal readonly struct BlockFace(int dx, int dy, int dz, float brightness, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
{
    /// <summary>Смещение к соседнему блоку, который может закрыть эту грань. Совпадает с нормалью грани.</summary>
    public readonly int Dx = dx, Dy = dy, Dz = dz;

    /// <summary>Постоянная яркость грани — множитель к свету и затенению (<see cref="ChunkVertex.Shade"/>): так видно объём.</summary>
    public readonly float Brightness = brightness;

    /// <summary>Углы единичного куба [0; 1]³ против часовой стрелки, если смотреть снаружи.</summary>
    public readonly Vector3 A = a, B = b, C = c, D = d;

    /// <summary>Текстурные координаты углов. v = 0 — верх картинки.</summary>
    public readonly Vector2 UvA = ComputeUv(dx, dy, dz, a),
                            UvB = ComputeUv(dx, dy, dz, b),
                            UvC = ComputeUv(dx, dy, dz, c),
                            UvD = ComputeUv(dx, dy, dz, d);

    public Vector3 Normal => new(Dx, Dy, Dz);

    public FaceKind Kind => Dy > 0 ? FaceKind.Top : Dy < 0 ? FaceKind.Bottom : FaceKind.Side;

    public static readonly BlockFace[] All =
    [
        new( 1,  0,  0, 0.6f, new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)), // +X восток
        new(-1,  0,  0, 0.6f, new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0)), // −X запад
        new( 0,  1,  0, 1.0f, new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0)), // +Y верх
        new( 0, -1,  0, 0.5f, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)), // −Y низ
        new( 0,  0,  1, 0.8f, new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)), // +Z юг
        new( 0,  0, -1, 0.8f, new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)), // −Z север
    ];

    /// <summary>
    /// UV угла грани. На боковых гранях u растёт слева направо, если смотреть на грань снаружи,
    /// а v = 1 − y, чтобы верх текстуры был у верха блока (полоса травы сверху, а не снизу).
    /// </summary>
    private static Vector2 ComputeUv(int dx, int dy, int dz, Vector3 corner) => (dx, dy, dz) switch
    {
        (1, 0, 0) => new(1 - corner.Z, 1 - corner.Y),  // смотрим с +X: справа −Z
        (-1, 0, 0) => new(corner.Z, 1 - corner.Y),     // смотрим с −X: справа +Z
        (0, 0, 1) => new(corner.X, 1 - corner.Y),      // смотрим с +Z: справа +X
        (0, 0, -1) => new(1 - corner.X, 1 - corner.Y), // смотрим с −Z: справа −X
        (0, 1, 0) => new(corner.X, corner.Z),
        (0, -1, 0) => new(corner.X, 1 - corner.Z),
        _ => throw new ArgumentException($"Нормаль грани ({dx}, {dy}, {dz}) должна быть единичной по одной оси."),
    };
}
