namespace Shared.Voxels;

/// <summary>Размеры чанка. Чанк — куб из <see cref="Size"/>³ блоков.</summary>
public static class ChunkMath
{
    /// <summary>log2 размера: перевод координат делается сдвигом, а не делением.</summary>
    public const int Bits = 5;
    public const int Size = 1 << Bits;      // 32
    public const int Mask = Size - 1;       // 31, младшие биты — позиция внутри чанка
    public const int Volume = Size * Size * Size; // 32768
}
