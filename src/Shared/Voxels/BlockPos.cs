using System.Numerics;

namespace Shared.Voxels;

/// <summary>Мировые координаты блока. Блок занимает куб от (X, Y, Z) до (X + 1, Y + 1, Z + 1).</summary>
public readonly record struct BlockPos(int X, int Y, int Z)
{
    // Арифметический сдвиг вправо округляет вниз и для отрицательных чисел: −1 >> 5 = −1.
    // Деление здесь было бы ошибкой: −1 / 32 = 0 (округление к нулю), блок попал бы не в тот чанк.
    public ChunkPos Chunk => new(X >> ChunkMath.Bits, Y >> ChunkMath.Bits, Z >> ChunkMath.Bits);

    // Побитовое И с маской даёт 0..31 и для отрицательных чисел: −1 & 31 = 31.
    // Остаток от деления (−1 % 32 = −1) здесь тоже был бы ошибкой.
    public LocalPos Local => new(X & ChunkMath.Mask, Y & ChunkMath.Mask, Z & ChunkMath.Mask);

    /// <summary>Блок, внутри которого лежит точка.</summary>
    public static BlockPos Containing(Vector3 point) =>
        new((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));

    public static BlockPos operator +(BlockPos a, BlockPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static BlockPos operator -(BlockPos a, BlockPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public override string ToString() => $"({X}, {Y}, {Z})";
}
