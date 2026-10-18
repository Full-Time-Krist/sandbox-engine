using Shared.Content;
using System.Buffers.Binary;
using System.IO.Compression;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

public class ChunkCodecTests
{
    private static readonly ChunkPos Position = new(-3, 1, 7);

    private static Chunk ChunkOf(Func<int, ushort> blockAt)
    {
        var blocks = new BlockId[ChunkMath.Volume];
        for (var i = 0; i < blocks.Length; i++)
            blocks[i] = new BlockId(blockAt(i));
        return Chunk.FromBlocks(Position, blocks);
    }

    private static Chunk RoundTrip(Chunk chunk) => ChunkCodec.Decode(chunk.Position, ChunkCodec.Encode(chunk));

    private static void AssertSameBlocks(Chunk expected, Chunk actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.NonAirCount, actual.NonAirCount);
        Assert.True(expected.Blocks.SequenceEqual(actual.Blocks), "Блоки после распаковки отличаются");
    }

    [Fact]
    public void EmptyChunk_RoundTrips_AndIsTiny()
    {
        var chunk = new Chunk(Position);

        var data = ChunkCodec.Encode(chunk);

        AssertSameBlocks(chunk, ChunkCodec.Decode(Position, data));
        Assert.True(data.Length < 16, $"Пустой чанк занял {data.Length} байт");
    }

    [Fact]
    public void UniformChunk_RoundTrips_AndIsTiny()
    {
        var chunk = ChunkOf(_ => 1);

        var data = ChunkCodec.Encode(chunk);

        AssertSameBlocks(chunk, ChunkCodec.Decode(Position, data));
        Assert.True(data.Length < 16, $"Однородный чанк занял {data.Length} байт");
    }

    [Fact]
    public void LayeredChunk_RoundTrips_AndCompressesWell()
    {
        // Как рельеф: камень, земля, трава, выше воздух.
        var chunk = ChunkOf(i => LocalPos.FromIndex(i).Y switch { < 10 => 1, < 13 => 2, 13 => 3, _ => 0 });

        var data = ChunkCodec.Encode(chunk);

        AssertSameBlocks(chunk, ChunkCodec.Decode(Position, data));
        Assert.True(data.Length < 1024, $"Слоистый чанк занял {data.Length} байт");
    }

    [Fact]
    public void NoisyChunk_RoundTrips()
    {
        var random = new Random(42);
        var chunk = ChunkOf(_ => (ushort)random.Next(7));

        AssertSameBlocks(chunk, RoundTrip(chunk));
    }

    [Fact]
    public void ChunkWithAllBaseBlocks_RoundTrips()
    {
        var registry = GameContent.Load().Blocks;
        var ids = registry.All.Select(b => b.Id.Value).ToArray();
        var chunk = ChunkOf(i => ids[i * 7919 % ids.Length]);

        AssertSameBlocks(chunk, RoundTrip(chunk));
    }

    /// <summary>Размеры палитры на границах разрядностей: каждый переход 0 → 1 → 2 → 4 → 8 → 16 бит.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(ChunkMath.Volume)]
    public void PaletteOfAnySize_RoundTrips(int kinds)
    {
        // Большие id, чтобы проверить и id блока, и его номер в палитре (они не совпадают).
        var chunk = ChunkOf(i => (ushort)(60000 - i % kinds));

        AssertSameBlocks(chunk, RoundTrip(chunk));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(256, 8)]
    [InlineData(257, 16)]
    [InlineData(ChunkMath.Volume, 16)]
    public void BitsFor_IsSmallestPowerOfTwoThatFits(int paletteSize, int expectedBits)
    {
        Assert.Equal(expectedBits, ChunkCodec.BitsFor(paletteSize));
    }

    [Fact]
    public void ConsecutiveEncodes_DoNotShareState()
    {
        // Палитра первого чанка не должна просочиться во второй (таблица поиска переиспользуется между вызовами).
        var first = ChunkOf(i => (ushort)(i % 2 == 0 ? 5 : 9));
        var second = ChunkOf(i => (ushort)(i % 3 == 0 ? 9 : 0));

        ChunkCodec.Encode(first);

        AssertSameBlocks(second, RoundTrip(second));
    }

    [Fact]
    public void EncodeIsDeterministic()
    {
        var chunk = ChunkOf(i => (ushort)(i % 5));

        Assert.Equal(ChunkCodec.Encode(chunk), ChunkCodec.Encode(chunk));
    }

    // --- Повреждённые данные: приходят из сети и с диска, поэтому должна быть понятная ошибка, а не мусор. ---

    /// <summary>Собирает данные в формате кодека из «сырого» содержимого, чтобы подсунуть декодеру что угодно.</summary>
    private static byte[] Pack(byte[] raw, byte version = ChunkCodec.Version)
    {
        using var output = new MemoryStream();
        output.WriteByte(version);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw);
        return output.ToArray();
    }

    private static byte[] Raw(ushort[] palette, int bits, int indexBytes, byte fill = 0)
    {
        var raw = new byte[2 + palette.Length * 2 + 1 + indexBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(raw, (ushort)palette.Length);
        for (var i = 0; i < palette.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2 + i * 2), palette[i]);
        raw[2 + palette.Length * 2] = (byte)bits;
        raw.AsSpan(3 + palette.Length * 2).Fill(fill);
        return raw;
    }

    private static void AssertRejected(byte[] data) =>
        Assert.Throws<InvalidDataException>(() => ChunkCodec.Decode(Position, data));

    [Fact]
    public void HandMadeValidData_Decodes()
    {
        // Проверка самих помощников: корректные данные, собранные вручную, читаются.
        var chunk = ChunkCodec.Decode(Position, Pack(Raw([0, 7], bits: 1, indexBytes: ChunkMath.Volume / 8, fill: 0b1010_1010)));

        Assert.Equal(BlockId.Air, chunk.Blocks[0]);
        Assert.Equal(new BlockId(7), chunk.Blocks[1]);
        Assert.Equal(ChunkMath.Volume / 2, chunk.NonAirCount);
    }

    [Fact]
    public void EmptyData_IsRejected() => AssertRejected([]);

    [Fact]
    public void UnknownVersion_IsRejected() => AssertRejected(Pack(Raw([1], 0, 0), version: 99));

    [Fact]
    public void Garbage_IsRejected() => AssertRejected([ChunkCodec.Version, 0xFF, 0xFF, 0xFF, 0x12, 0x34]);

    [Fact]
    public void ZeroPalette_IsRejected() => AssertRejected(Pack(Raw([], 0, 0)));

    [Fact]
    public void WrongBits_IsRejected() => AssertRejected(Pack(Raw([0, 1, 2], bits: 4, indexBytes: ChunkMath.Volume / 2)));

    [Fact]
    public void TruncatedIndices_IsRejected() => AssertRejected(Pack(Raw([0, 1], bits: 1, indexBytes: ChunkMath.Volume / 8 - 1)));

    [Fact]
    public void ExtraBytes_AreRejected() => AssertRejected(Pack(Raw([0, 1], bits: 1, indexBytes: ChunkMath.Volume / 8 + 1)));

    [Fact]
    public void TruncatedCompressedStream_IsRejected()
    {
        var data = ChunkCodec.Encode(ChunkOf(i => (ushort)(i % 5)));

        AssertRejected(data[..(data.Length / 2)]);
    }

    [Fact]
    public void IndexOutsidePalette_IsRejected()
    {
        // Палитра из 3 блоков требует 2 бита, а 2 бита позволяют индекс 3 — его в палитре нет.
        AssertRejected(Pack(Raw([0, 1, 2], bits: 2, indexBytes: ChunkMath.Volume / 4, fill: 0b11_11_11_11)));
    }

    [Fact]
    public void HugeDecompressedData_IsRejected()
    {
        // «Zip-бомба»: несколько килобайт, которые распаковываются в мегабайты. Заголовок корректный,
        // так что отвергнуть данные может только ограничение размера, а не другие проверки.
        var bomb = Pack(Raw([0, 1], bits: 1, indexBytes: 4 * 1024 * 1024));

        var error = Assert.Throws<InvalidDataException>(() => ChunkCodec.Decode(Position, bomb));
        Assert.Contains("длиннее допустимого", error.Message);
    }
}
