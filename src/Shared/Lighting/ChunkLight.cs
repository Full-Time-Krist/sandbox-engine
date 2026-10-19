using Shared.Voxels;

namespace Shared.Lighting;

/// <summary>
/// Свет каждого блока чанка: два канала по 4 бита в одном байте — небесный (старшие биты) и блочный (младшие),
/// оба от 0 до <see cref="Max"/>. Индексы — как у блоков (<see cref="LocalPos.Index"/>).
/// </summary>
/// <remarks>
/// Пока весь чанк освещён одинаково (воздух под открытым небом, толща камня), массив не заводится: хранится одно
/// значение. Таких чанков большинство, а 32 КБ на каждый удвоили бы память мира. Массив появляется при первой
/// записи, отличной от общего значения, и больше не убирается.
/// </remarks>
public sealed class ChunkLight
{
    /// <summary>Наибольший уровень света: полное солнце, самый яркий источник.</summary>
    public const int Max = 15;

    private byte _uniform;
    private byte[]? _values;

    /// <summary>Одинаков ли свет во всём чанке (тогда он равен <see cref="UniformValue"/>).</summary>
    public bool IsUniform => _values is null;

    /// <summary>Общий упакованный свет одинаково освещённого чанка.</summary>
    public byte UniformValue => _uniform;

    public int GetSky(int index) => Get(index) >> 4;

    public int GetBlock(int index) => Get(index) & 0x0F;

    /// <summary>Упакованный свет блока: небесный × 16 + блочный.</summary>
    public byte Get(int index) => _values is { } values ? values[index] : _uniform;

    public void SetSky(int index, int level) => Set(index, (byte)((level << 4) | (Get(index) & 0x0F)));

    public void SetBlock(int index, int level) => Set(index, (byte)((Get(index) & 0xF0) | level));

    public void Set(int index, byte packed)
    {
        if (_values is null)
        {
            if (packed == _uniform)
                return;
            _values = new byte[ChunkMath.Volume];
            Array.Fill(_values, _uniform);
        }
        _values[index] = packed;
    }

    /// <summary>Весь чанк — одним значением; массив, если был, отбрасывается.</summary>
    public void Fill(byte packed)
    {
        _values = null;
        _uniform = packed;
    }

    /// <summary>
    /// Свет всех блоков из <paramref name="source"/> (длина — <see cref="ChunkMath.Volume"/>), например пришедший с сервера.
    /// Одинаковый во всём чанке хранится одним значением, без массива.
    /// </summary>
    public void CopyFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length != ChunkMath.Volume)
            throw new ArgumentException($"Ожидалось {ChunkMath.Volume} значений, получено {source.Length}.", nameof(source));

        if (source.IndexOfAnyExcept(source[0]) < 0)
        {
            Fill(source[0]);
            return;
        }
        _values ??= new byte[ChunkMath.Volume];
        source.CopyTo(_values);
    }

    /// <summary>Копирует свет блоков с индексами от <paramref name="start"/> подряд — по длине <paramref name="destination"/>.</summary>
    public void CopyTo(int start, Span<byte> destination)
    {
        if (_values is { } values)
            values.AsSpan(start, destination.Length).CopyTo(destination);
        else
            destination.Fill(_uniform);
    }

    /// <summary>Копирует свет всех блоков в <paramref name="destination"/> (длина — <see cref="ChunkMath.Volume"/>).</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (_values is { } values)
            values.CopyTo(destination);
        else
            destination[..ChunkMath.Volume].Fill(_uniform);
    }

    public static byte Pack(int sky, int block) => (byte)((sky << 4) | block);
}
