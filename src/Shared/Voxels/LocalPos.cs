namespace Shared.Voxels;

/// <summary>Координаты блока внутри чанка, каждая в диапазоне [0; <see cref="ChunkMath.Size"/>).</summary>
/// <remarks>
/// Конструктор диапазон не проверяет: <see cref="LocalPos"/> создаётся в горячих циклах построения меша.
/// Для проверки внешних данных есть <see cref="IsInBounds"/>.
/// </remarks>
public readonly record struct LocalPos(int X, int Y, int Z)
{
    /// <summary>
    /// Индекс в плоском массиве блоков чанка: X меняется быстрее всего, затем Z, затем Y.
    /// Так соседние по X блоки лежат в памяти подряд, а горизонтальный слой — одним куском.
    /// </summary>
    public int Index => X + (Z << ChunkMath.Bits) + (Y << (ChunkMath.Bits * 2));

    public bool IsInBounds =>
        (uint)X < ChunkMath.Size && (uint)Y < ChunkMath.Size && (uint)Z < ChunkMath.Size;

    public static LocalPos FromIndex(int index) =>
        new(index & ChunkMath.Mask, index >> (ChunkMath.Bits * 2), (index >> ChunkMath.Bits) & ChunkMath.Mask);

    public override string ToString() => $"local ({X}, {Y}, {Z})";
}
