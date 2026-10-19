using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Shared.Voxels;

/// <summary>
/// Общая часть кодеков чанка (<see cref="ChunkCodec"/> — блоки, <see cref="Lighting.LightCodec"/> — свет):
/// значения всех блоков чанка как палитра + упакованные индексы + Deflate.
/// </summary>
/// <remarks>
/// <para>
/// В чанке 32 768 значений, но разных среди них обычно единицы. Палитра — список разных значений, а вместо каждого
/// хранится номер в палитре минимальной нужной разрядности: при 5 разных значениях это 4 бита вместо 16.
/// Deflate затем сжимает повторы (целые слои камня или воздуха).
/// </para>
/// <para>
/// Формат: байт версии (без сжатия, чтобы старые данные можно было распознать), затем поток Deflate:
/// <c>ushort</c> число элементов палитры, палитра (<c>ushort</c> значения), байт разрядности индекса,
/// индексы в порядке <see cref="LocalPos.Index"/>. Числа — little-endian.
/// </para>
/// <para>
/// Разрядность — только 0, 1, 2, 4, 8 или 16 бит: индекс никогда не пересекает границу байта, код проще и быстрее.
/// Неиспользованные биты дёшево сжимает Deflate. Внутри байта индексы идут с младших битов.
/// Однородный чанк — палитра из одного значения и 0 бит, индексов нет совсем.
/// </para>
/// </remarks>
internal static class PaletteCodec
{
    /// <summary>
    /// Таблица «значение → номер в палитре + 1» (0 — значения ещё нет в палитре). Своя у каждого потока:
    /// кодирование идёт и в фоновых потоках. После кодирования обнуляются только занятые ячейки,
    /// поэтому таблицу не нужно чистить целиком (256 КБ) каждый раз.
    /// </summary>
    [ThreadStatic] private static int[]? t_paletteLookup;

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

    /// <summary>Наибольший размер распакованных данных: палитра наибольшего размера и индексы её разрядности.</summary>
    private static int MaxRawSize(int maxPaletteSize) => 2 + maxPaletteSize * 2 + 1 + ChunkMath.Volume * BitsFor(maxPaletteSize) / 8;

    /// <param name="values">Все значения чанка в порядке <see cref="LocalPos.Index"/>.</param>
    public static byte[] Encode(byte version, ReadOnlySpan<ushort> values)
    {
        if (values.Length != ChunkMath.Volume)
            throw new ArgumentException($"Ожидалось {ChunkMath.Volume} значений, получено {values.Length}.", nameof(values));

        var lookup = t_paletteLookup ??= new int[ushort.MaxValue + 1];
        var palette = new List<ushort>();
        var indices = ArrayPool<ushort>.Shared.Rent(ChunkMath.Volume);
        var raw = ArrayPool<byte>.Shared.Rent(MaxRawSize(ChunkMath.Volume));
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                var slot = lookup[value];
                if (slot == 0)
                {
                    palette.Add(value);
                    slot = palette.Count;
                    lookup[value] = slot;
                }
                indices[i] = (ushort)(slot - 1);
            }

            var bits = BitsFor(palette.Count);
            var length = WriteRaw(raw, palette, bits, indices.AsSpan(0, ChunkMath.Volume));

            using var output = new MemoryStream();
            output.WriteByte(version);
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(raw, 0, length);
            return output.ToArray();
        }
        finally
        {
            foreach (var value in palette)
                lookup[value] = 0;
            ArrayPool<ushort>.Shared.Return(indices);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>Распаковывает значения чанка в <paramref name="destination"/>. Возвращает размер палитры.</summary>
    /// <param name="maxPaletteSize">Сколько разных значений допустимо: больше — данные испорчены.</param>
    /// <param name="what">Чьи данные — для текста ошибки («чанка», «света»).</param>
    /// <exception cref="InvalidDataException">Данные повреждены или в незнакомом формате.</exception>
    /// <remarks>
    /// Данные приходят из сети и с диска, поэтому ничему в них не верим: проверяем версию, размеры
    /// и каждый индекс. Повреждённые данные должны давать понятную ошибку, а не мусор в мире или зависание.
    /// </remarks>
    public static int Decode(ReadOnlyMemory<byte> data, byte version, int maxPaletteSize, string what, Span<ushort> destination)
    {
        if (destination.Length != ChunkMath.Volume)
            throw new ArgumentException($"Нужно место под {ChunkMath.Volume} значений.", nameof(destination));
        if (data.IsEmpty)
            throw new InvalidDataException($"Пустые данные {what}.");
        if (data.Span[0] != version)
            throw new InvalidDataException($"Неизвестная версия формата {what}: {data.Span[0]}.");

        // Читаем на байт больше максимума: если он прочитался, данные длиннее допустимого
        // (так не даём «zip-бомбе» распаковаться в гигабайты).
        var maxRawSize = MaxRawSize(maxPaletteSize);
        var raw = ArrayPool<byte>.Shared.Rent(maxRawSize + 1);
        try
        {
            var length = Inflate(data[1..], raw.AsSpan(0, maxRawSize + 1));
            if (length > maxRawSize)
                throw new InvalidDataException($"Распакованные данные {what} длиннее допустимого.");
            return ReadRaw(raw.AsSpan(0, length), maxPaletteSize, what, destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static int WriteRaw(Span<byte> raw, List<ushort> palette, int bits, ReadOnlySpan<ushort> indices)
    {
        var offset = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(raw[offset..], (ushort)palette.Count);
        offset += 2;
        foreach (var value in palette)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw[offset..], value);
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

    private static int ReadRaw(ReadOnlySpan<byte> raw, int maxPaletteSize, string what, Span<ushort> destination)
    {
        if (raw.Length < 3)
            throw new InvalidDataException($"Данные {what} обрываются в заголовке.");

        var paletteSize = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        if (paletteSize == 0 || paletteSize > maxPaletteSize)
            throw new InvalidDataException($"Неверный размер палитры {what}: {paletteSize}.");

        var offset = 2;
        if (raw.Length < offset + paletteSize * 2 + 1)
            throw new InvalidDataException($"Данные {what} обрываются в палитре.");

        Span<ushort> palette = paletteSize <= 256 ? stackalloc ushort[paletteSize] : new ushort[paletteSize];
        for (var i = 0; i < paletteSize; i++)
        {
            palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw[offset..]);
            offset += 2;
        }

        // Разрядность должна быть ровно той, что выбрал бы кодировщик: так формат однозначен,
        // а индекс заведомо не может указать за пределы палитры больше, чем на разрядность.
        int bits = raw[offset++];
        if (bits != BitsFor(paletteSize))
            throw new InvalidDataException($"Разрядность {bits} не подходит к палитре {what} из {paletteSize} значений.");

        var packed = raw[offset..];
        if (packed.Length != ChunkMath.Volume * bits / 8)
            throw new InvalidDataException($"Ожидалось {ChunkMath.Volume * bits / 8} байт индексов {what}, получено {packed.Length}.");

        if (bits == 0)
        {
            destination.Fill(palette[0]);
            return paletteSize;
        }

        var mask = (1 << bits) - 1;
        var perByte = bits == 16 ? 0 : 8 / bits;
        for (var i = 0; i < destination.Length; i++)
        {
            var index = bits == 16
                ? BinaryPrimitives.ReadUInt16LittleEndian(packed[(i * 2)..])
                : (packed[i / perByte] >> (i % perByte * bits)) & mask;

            // При разрядности 4 палитра может быть из 5 значений, а индекс — до 15.
            if (index >= paletteSize)
                throw new InvalidDataException($"Индекс {index} за пределами палитры {what} из {paletteSize} значений.");
            destination[i] = palette[index];
        }
        return paletteSize;
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
