using System.Buffers;
using Shared.Voxels;

namespace Shared.Lighting;

/// <summary>
/// Сжатие света чанка для передачи клиенту: тот же формат, что у блоков (<see cref="PaletteCodec"/>: палитра +
/// индексы + Deflate), значения — упакованный свет блока (<see cref="ChunkLight.Get"/>, 0–255). В сохранение свет
/// не пишется: сервер считает его заново при загрузке.
/// </summary>
/// <remarks>
/// Свет сжимается хорошо: одинаково освещённый чанк (воздух под солнцем, толща камня) — палитра из одного значения,
/// а у поверхности разных уровней два-три десятка, и они идут слоями.
/// </remarks>
public static class LightCodec
{
    public const byte Version = 1;

    /// <summary>Разных упакованных значений света — не больше 256: по 4 бита на небесный и блочный.</summary>
    private const int MaxPaletteSize = 256;

    private const string What = "света";

    public static byte[] Encode(ChunkLight light)
    {
        var values = ArrayPool<ushort>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var span = values.AsSpan(0, ChunkMath.Volume);
            if (light.IsUniform)
            {
                span.Fill(light.UniformValue);
            }
            else
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = light.Get(i);
            }
            return PaletteCodec.Encode(Version, span);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(values);
        }
    }

    /// <summary>Распаковывает свет чанка в <paramref name="destination"/> (весь свет чанка заменяется).</summary>
    /// <exception cref="InvalidDataException">Данные повреждены или в незнакомом формате; <paramref name="destination"/> тогда не меняется.</exception>
    public static void Decode(ReadOnlyMemory<byte> data, ChunkLight destination)
    {
        var values = ArrayPool<ushort>.Shared.Rent(ChunkMath.Volume);
        var bytes = ArrayPool<byte>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var span = values.AsSpan(0, ChunkMath.Volume);
            if (PaletteCodec.Decode(data, Version, MaxPaletteSize, What, span) == 1 && span[0] <= byte.MaxValue)
            {
                destination.Fill((byte)span[0]);
                return;
            }

            var packed = bytes.AsSpan(0, ChunkMath.Volume);
            for (var i = 0; i < span.Length; i++)
            {
                // Значение из палитры — упакованный свет: больше байта быть не может.
                if (span[i] > byte.MaxValue)
                    throw new InvalidDataException($"Неверное значение света: {span[i]}.");
                packed[i] = (byte)span[i];
            }
            destination.CopyFrom(packed);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(values);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
