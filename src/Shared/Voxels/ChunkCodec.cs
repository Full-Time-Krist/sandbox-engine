using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Shared.Blocks;

namespace Shared.Voxels;

/// <summary>
/// Сжатие блоков чанка: палитра + упакованные индексы + Deflate. Один формат для сети и сохранений.
/// </summary>
/// <remarks>
/// <para>
/// В чанке 32 768 блоков, но разных среди них обычно единицы. Палитра — список разных блоков чанка,
/// а вместо каждого блока хранится номер в палитре минимальной нужной разрядности: при 5 видах блоков
/// это 4 бита вместо 16. Deflate затем сжимает повторы (целые слои камня или воздуха).
/// </para>
/// <para>
/// Формат: байт версии (без сжатия, чтобы старые сохранения можно было распознать), затем поток Deflate:
/// <c>ushort</c> число элементов палитры, палитра (<c>ushort</c> id блоков), байт разрядности индекса,
/// индексы в порядке <see cref="LocalPos.Index"/>. Числа — little-endian.
/// </para>
/// <para>
/// Разрядность — только 0, 1, 2, 4, 8 или 16 бит: индекс никогда не пересекает границу байта, код проще и быстрее.
/// Неиспользованные биты дёшево сжимает Deflate. Внутри байта индексы идут с младших битов.
/// Однородный чанк (например, из одного воздуха) — палитра из одного блока и 0 бит, индексов нет совсем.
/// </para>
/// </remarks>
public static class ChunkCodec
{
    public const byte Version = 1;

    private const int MaxPaletteSize = ChunkMath.Volume;

    /// <summary>Наибольший размер распакованных данных: палитра из всех разных блоков и 16-битные индексы.</summary>
    private const int MaxRawSize = 2 + MaxPaletteSize * 2 + 1 + ChunkMath.Volume * 2;

    /// <summary>
    /// Таблица «id блока → номер в палитре + 1» (0 — блока ещё нет в палитре). Своя у каждого потока:
    /// кодирование идёт и в фоновых потоках. После кодирования обнуляются только занятые ячейки,
    /// поэтому таблицу не нужно чистить целиком (256 КБ) каждый раз.
    /// </summary>
    [ThreadStatic] private static int[]? t_paletteLookup;

    public static byte[] Encode(Chunk chunk) => Encode(chunk.Blocks);

    /// <param name="blocks">Все блоки чанка в порядке <see cref="LocalPos.Index"/>.</param>
    public static byte[] Encode(ReadOnlySpan<BlockId> blocks)
    {
        if (blocks.Length != ChunkMath.Volume)
            throw new ArgumentException($"Ожидалось {ChunkMath.Volume} блоков, получено {blocks.Length}.", nameof(blocks));

        var lookup = t_paletteLookup ??= new int[ushort.MaxValue + 1];
        var palette = new List<ushort>();
        var indices = ArrayPool<ushort>.Shared.Rent(ChunkMath.Volume);
        var raw = ArrayPool<byte>.Shared.Rent(MaxRawSize);
        try
        {
            for (var i = 0; i < blocks.Length; i++)
            {
                var id = blocks[i].Value;
                var slot = lookup[id];
                if (slot == 0)
                {
                    palette.Add(id);
                    slot = palette.Count;
                    lookup[id] = slot;
                }
                indices[i] = (ushort)(slot - 1);
            }

            var bits = BitsFor(palette.Count);
            var length = WriteRaw(raw, palette, bits, indices.AsSpan(0, ChunkMath.Volume));

            using var output = new MemoryStream();
            output.WriteByte(Version);
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(raw, 0, length);
            return output.ToArray();
        }
        finally
        {
            foreach (var id in palette)
                lookup[id] = 0;
            ArrayPool<ushort>.Shared.Return(indices);
            ArrayPool<byte>.Shared.Return(raw);
        }
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
    /// <remarks>
    /// Данные приходят из сети и с диска, поэтому ничему в них не верим: проверяем версию, размеры
    /// и каждый индекс. Повреждённый чанк должен давать понятную ошибку, а не мусор в мире или зависание.
    /// </remarks>
    public static void Decode(ReadOnlyMemory<byte> data, Span<BlockId> destination)
    {
        if (destination.Length != ChunkMath.Volume)
            throw new ArgumentException($"Нужно место под {ChunkMath.Volume} блоков.", nameof(destination));
        if (data.IsEmpty)
            throw new InvalidDataException("Пустые данные чанка.");
        if (data.Span[0] != Version)
            throw new InvalidDataException($"Неизвестная версия формата чанка: {data.Span[0]}.");

        // Читаем на байт больше максимума: если он прочитался, данные длиннее допустимого
        // (так не даём «zip-бомбе» распаковаться в гигабайты).
        var raw = ArrayPool<byte>.Shared.Rent(MaxRawSize + 1);
        try
        {
            var length = Inflate(data[1..], raw.AsSpan(0, MaxRawSize + 1));
            if (length > MaxRawSize)
                throw new InvalidDataException("Распакованные данные чанка длиннее допустимого.");
            ReadRaw(raw.AsSpan(0, length), destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>Наименьшая разрядность из 0, 1, 2, 4, 8, 16, в которую помещается номер в палитре такого размера.</summary>
    public static int BitsFor(int paletteSize) => paletteSize switch
    {
        <= 1 => 0,
        <= 2 => 1,
        <= 4 => 2,
        <= 16 => 4,
        <= 256 => 8,
        _ => 16,
    };

    private static int WriteRaw(Span<byte> raw, List<ushort> palette, int bits, ReadOnlySpan<ushort> indices)
    {
        var offset = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(raw[offset..], (ushort)palette.Count);
        offset += 2;
        foreach (var id in palette)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw[offset..], id);
            offset += 2;
        }
        raw[offset++] = (byte)bits;

        var packed = raw.Slice(offset, ChunkMath.Volume * bits / 8);
        if (bits == 16)
        {
            for (var i = 0; i < indices.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(packed[(i * 2)..], indices[i]);
        }
        else if (bits > 0)
        {
            // В байт помещается 8 / bits индексов; i-й индекс байта сдвинут на i * bits.
            var perByte = 8 / bits;
            packed.Clear();
            for (var i = 0; i < indices.Length; i++)
                packed[i / perByte] |= (byte)(indices[i] << (i % perByte * bits));
        }

        return offset + packed.Length;
    }

    private static void ReadRaw(ReadOnlySpan<byte> raw, Span<BlockId> destination)
    {
        if (raw.Length < 3)
            throw new InvalidDataException("Данные чанка обрываются в заголовке.");

        var paletteSize = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        if (paletteSize is 0 or > MaxPaletteSize)
            throw new InvalidDataException($"Неверный размер палитры: {paletteSize}.");

        var offset = 2;
        if (raw.Length < offset + paletteSize * 2 + 1)
            throw new InvalidDataException("Данные чанка обрываются в палитре.");

        Span<BlockId> palette = paletteSize <= 256 ? stackalloc BlockId[paletteSize] : new BlockId[paletteSize];
        for (var i = 0; i < paletteSize; i++)
        {
            palette[i] = new BlockId(BinaryPrimitives.ReadUInt16LittleEndian(raw[offset..]));
            offset += 2;
        }

        // Разрядность должна быть ровно той, что выбрал бы кодировщик: так формат однозначен,
        // а индекс заведомо не может указать за пределы палитры больше, чем на разрядность.
        int bits = raw[offset++];
        if (bits != BitsFor(paletteSize))
            throw new InvalidDataException($"Разрядность {bits} не подходит к палитре из {paletteSize} блоков.");

        var packed = raw[offset..];
        if (packed.Length != ChunkMath.Volume * bits / 8)
            throw new InvalidDataException($"Ожидалось {ChunkMath.Volume * bits / 8} байт индексов, получено {packed.Length}.");

        if (bits == 0)
        {
            destination.Fill(palette[0]);
            return;
        }

        var mask = (1 << bits) - 1;
        var perByte = bits == 16 ? 0 : 8 / bits;
        for (var i = 0; i < destination.Length; i++)
        {
            var index = bits == 16
                ? BinaryPrimitives.ReadUInt16LittleEndian(packed[(i * 2)..])
                : (packed[i / perByte] >> (i % perByte * bits)) & mask;

            // При разрядности 4 палитра может быть из 5 блоков, а индекс — до 15.
            if (index >= paletteSize)
                throw new InvalidDataException($"Индекс {index} за пределами палитры из {paletteSize} блоков.");
            destination[i] = palette[index];
        }
    }

    private static int Inflate(ReadOnlyMemory<byte> compressed, Span<byte> output)
    {
        // MemoryStream читает массив без копирования; Memory от массива или ImmutableArray всегда им и является.
        if (!MemoryMarshal.TryGetArray(compressed, out var segment))
            segment = compressed.ToArray();

        using var input = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var total = 0;
        int read;
        while (total < output.Length && (read = deflate.Read(output[total..])) > 0)
            total += read;
        return total;
    }
}
