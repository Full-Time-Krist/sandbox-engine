using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using Shared.Blocks;
using Shared.Lighting;
using Shared.Protocol;
using Shared.Voxels;

namespace Shared.Tests.Lighting;

public class LightCodecTests
{
    private static ChunkLight LightOf(Func<int, byte> packedAt)
    {
        var values = new byte[ChunkMath.Volume];
        for (var i = 0; i < values.Length; i++)
            values[i] = packedAt(i);
        var light = new ChunkLight();
        light.CopyFrom(values);
        return light;
    }

    private static ChunkLight RoundTrip(ChunkLight light)
    {
        var restored = new ChunkLight();
        LightCodec.Decode(LightCodec.Encode(light), restored);
        return restored;
    }

    private static void AssertSameLight(ChunkLight expected, ChunkLight actual)
    {
        for (var i = 0; i < ChunkMath.Volume; i++)
        {
            if (expected.Get(i) != actual.Get(i))
                Assert.Fail($"Свет блока {LocalPos.FromIndex(i)}: {actual.Get(i)} вместо {expected.Get(i)}");
        }
    }

    /// <summary>Как у поверхности: сверху солнце, ниже оно гаснет по слоям, в толще темно, кое-где свет факела.</summary>
    private static ChunkLight SurfaceLight() => LightOf(i =>
    {
        var pos = LocalPos.FromIndex(i);
        var sky = Math.Clamp(pos.Y - 8, 0, 15);
        var block = Math.Max(0, 14 - Math.Abs(pos.X - 16) - Math.Abs(pos.Y - 10) - Math.Abs(pos.Z - 16));
        return ChunkLight.Pack(sky, block);
    });

    [Fact]
    public void UniformLight_RoundTrips_StaysUniform_AndIsTiny()
    {
        var light = new ChunkLight();
        light.Fill(ChunkLight.Pack(15, 0));

        var data = LightCodec.Encode(light);
        var restored = new ChunkLight();
        LightCodec.Decode(data, restored);

        Assert.True(restored.IsUniform, "Одинаковый свет после распаковки занял массив");
        Assert.Equal(ChunkLight.Pack(15, 0), restored.UniformValue);
        Assert.True(data.Length < 16, $"Одинаковый свет занял {data.Length} байт");
    }

    [Fact]
    public void SurfaceLight_RoundTrips_AndCompressesWell()
    {
        var light = SurfaceLight();

        var data = LightCodec.Encode(light);
        var restored = new ChunkLight();
        LightCodec.Decode(data, restored);

        Assert.False(restored.IsUniform);
        AssertSameLight(light, restored);
        Assert.True(data.Length < 2048, $"Свет у поверхности занял {data.Length} байт");
    }

    [Fact]
    public void AllPackedValues_RoundTrip()
    {
        // Все 256 сочетаний небесного и блочного — наибольшая палитра света, 8 бит на блок.
        var light = LightOf(i => (byte)(i * 37 % 256));

        AssertSameLight(light, RoundTrip(light));
    }

    [Fact]
    public void Decode_ReplacesWholeLight()
    {
        var destination = SurfaceLight();
        var uniform = new ChunkLight();
        uniform.Fill(ChunkLight.Pack(0, 3));

        LightCodec.Decode(LightCodec.Encode(uniform), destination);

        Assert.True(destination.IsUniform);
        Assert.Equal(ChunkLight.Pack(0, 3), destination.UniformValue);

        LightCodec.Decode(LightCodec.Encode(SurfaceLight()), destination);
        AssertSameLight(SurfaceLight(), destination);
    }

    [Fact]
    public void ChunkData_CarriesLight()
    {
        var chunk = new Chunk(new ChunkPos(-2, 0, 5));
        chunk.Set(new LocalPos(3, 4, 5), new BlockId(1));
        chunk.Light.CopyFrom(Values(SurfaceLight()));

        var restored = ChunkData.From(chunk).ToChunk();

        Assert.True(chunk.Blocks.SequenceEqual(restored.Blocks));
        AssertSameLight(chunk.Light, restored.Light);
    }

    [Fact]
    public void ChunkLightChanged_CarriesLight()
    {
        var chunk = new Chunk(new ChunkPos(1, -1, 0));
        chunk.Light.CopyFrom(Values(SurfaceLight()));

        var message = ChunkLightChanged.From(chunk);
        var restored = new ChunkLight();
        LightCodec.Decode(message.Light.AsMemory(), restored);

        Assert.Equal(chunk.Position, message.Position);
        AssertSameLight(chunk.Light, restored);
    }

    private static byte[] Values(ChunkLight light)
    {
        var values = new byte[ChunkMath.Volume];
        light.CopyTo(values);
        return values;
    }

    // --- Повреждённые данные: свет приходит из сети, поэтому — понятная ошибка, а не мусор. ---

    private static byte[] Pack(byte[] raw, byte version = LightCodec.Version)
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

    /// <summary>Испорченные данные отвергаются, а свет, куда распаковывали, остаётся прежним.</summary>
    private static void AssertRejected(byte[] data)
    {
        var destination = SurfaceLight();

        Assert.Throws<InvalidDataException>(() => LightCodec.Decode(data, destination));

        AssertSameLight(SurfaceLight(), destination);
    }

    [Fact]
    public void HandMadeValidData_Decodes()
    {
        // Проверка самих помощников: корректные данные, собранные вручную, читаются.
        var light = new ChunkLight();
        LightCodec.Decode(Pack(Raw([0xF0, 0x0E], bits: 1, indexBytes: ChunkMath.Volume / 8, fill: 0b1010_1010)), light);

        Assert.Equal(15, light.GetSky(0));
        Assert.Equal(14, light.GetBlock(1));
        Assert.Equal(0, light.GetSky(1));
    }

    [Fact]
    public void EmptyData_IsRejected() => AssertRejected([]);

    [Fact]
    public void UnknownVersion_IsRejected() => AssertRejected(Pack(Raw([0xF0], 0, 0), version: 99));

    [Fact]
    public void BlockData_IsRejected()
    {
        // Блоки чанка вместо света: значения палитры (id блоков) больше байта.
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(0, 0, 0), new BlockId(300));

        AssertRejected(ChunkCodec.Encode(chunk));
    }

    [Fact]
    public void ValueAboveByte_IsRejected() => AssertRejected(Pack(Raw([0xF0, 256], bits: 1, indexBytes: ChunkMath.Volume / 8, fill: 0xFF)));

    [Fact]
    public void UniformValueAboveByte_IsRejected() => AssertRejected(Pack(Raw([0x1F0], 0, 0)));

    [Fact]
    public void PaletteLargerThanAllValues_IsRejected()
    {
        // Разных упакованных значений света — не больше 256.
        var palette = Enumerable.Range(0, 257).Select(i => (ushort)i).ToArray();

        // Без индексов: иначе данные отверг бы предел распаковки, не дойдя до палитры.
        AssertRejected(Pack(Raw(palette, bits: 16, indexBytes: 0)));
    }

    [Fact]
    public void WrongBits_IsRejected() => AssertRejected(Pack(Raw([0, 1, 2], bits: 4, indexBytes: ChunkMath.Volume / 2)));

    [Fact]
    public void TruncatedIndices_IsRejected() => AssertRejected(Pack(Raw([0, 1], bits: 1, indexBytes: ChunkMath.Volume / 8 - 1)));

    [Fact]
    public void IndexOutsidePalette_IsRejected() =>
        AssertRejected(Pack(Raw([0, 1, 2], bits: 2, indexBytes: ChunkMath.Volume / 4, fill: 0b11_11_11_11)));

    [Fact]
    public void TruncatedCompressedStream_IsRejected()
    {
        var data = LightCodec.Encode(SurfaceLight());

        AssertRejected(data[..(data.Length / 2)]);
    }

    [Fact]
    public void HugeDecompressedData_IsRejected()
    {
        var bomb = Pack(Raw([0, 1], bits: 1, indexBytes: 4 * 1024 * 1024));

        var error = Assert.Throws<InvalidDataException>(() => LightCodec.Decode(bomb, new ChunkLight()));
        Assert.Contains("длиннее допустимого", error.Message);
    }

    [Fact]
    public void ChunkData_WithBrokenLight_IsRejected()
    {
        var good = ChunkData.From(new Chunk(new ChunkPos(0, 1, 0)));
        var broken = good with { Light = ImmutableArray.Create<byte>(LightCodec.Version, 0xFF, 0x12) };

        Assert.Throws<InvalidDataException>(() => broken.ToChunk());
    }
}
