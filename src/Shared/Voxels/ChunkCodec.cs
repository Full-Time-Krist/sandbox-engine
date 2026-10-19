using System.Buffers;
using System.Runtime.InteropServices;
using Shared.Blocks;

namespace Shared.Voxels;

/// <summary>
/// Сжатие блоков чанка: палитра + упакованные индексы + Deflate (<see cref="PaletteCodec"/>, значения — id блоков).
/// Один формат для сети и сохранений.
/// </summary>
/// <remarks>
/// В чанке 32 768 блоков, но разных среди них обычно единицы: при 5 видах блоков индекс занимает 4 бита вместо 16,
/// а Deflate сжимает целые слои камня или воздуха. Однородный чанк (например, из одного воздуха) — 8 байт.
/// </remarks>
public static class ChunkCodec
{
    public const byte Version = 1;

    private const string What = "чанка";

    public static byte[] Encode(Chunk chunk) => Encode(chunk.Blocks);

    /// <param name="blocks">Все блоки чанка в порядке <see cref="LocalPos.Index"/>.</param>
    public static byte[] Encode(ReadOnlySpan<BlockId> blocks)
    {
        if (blocks.Length != ChunkMath.Volume)
            throw new ArgumentException($"Ожидалось {ChunkMath.Volume} блоков, получено {blocks.Length}.", nameof(blocks));
        return PaletteCodec.Encode(Version, MemoryMarshal.Cast<BlockId, ushort>(blocks));
    }

    public static Chunk Decode(ChunkPos position, ReadOnlyMemory<byte> data)
    {
        var blocks = ArrayPool<BlockId>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var span = blocks.AsSpan(0, ChunkMath.Volume);
            Decode(data, span);
            return Chunk.FromBlocks(position, span);
        }
        finally
        {
            ArrayPool<BlockId>.Shared.Return(blocks);
        }
    }

    /// <summary>Распаковывает блоки чанка в <paramref name="destination"/>.</summary>
    /// <exception cref="InvalidDataException">Данные повреждены или в незнакомом формате.</exception>
    public static void Decode(ReadOnlyMemory<byte> data, Span<BlockId> destination)
    {
        if (destination.Length != ChunkMath.Volume)
            throw new ArgumentException($"Нужно место под {ChunkMath.Volume} блоков.", nameof(destination));
        PaletteCodec.Decode(data, Version, ChunkMath.Volume, What, MemoryMarshal.Cast<BlockId, ushort>(destination));
    }

    /// <summary>Наименьшая разрядность из 0, 1, 2, 4, 8, 16, в которую помещается номер в палитре такого размера.</summary>
    public static int BitsFor(int paletteSize) => PaletteCodec.BitsFor(paletteSize);
}
