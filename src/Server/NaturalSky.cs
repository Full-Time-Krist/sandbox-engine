using System.Collections.Concurrent;
using Shared.Lighting;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Солнце над незагруженными чанками на сервере: открыто, если выше естественного рельефа и деревьев
/// (<see cref="TerrainGenerator.NaturalTops"/>). Постройки игроков в незагруженных чанках не учитываются — их без
/// чанка не видно (открытый вопрос этапа 8).
/// </summary>
/// <remarks>
/// Высоты столбца чанков считает шум — около миллисекунды на столбец. Чтобы не тратить на это тик сервера, их заранее
/// считают потоки генерации (<see cref="Prepare"/>), а <see cref="IsOpen"/> из потока сервера обычно только читает кеш.
/// </remarks>
internal sealed class NaturalSky(TerrainGenerator terrain) : ISkyAbove
{
    /// <summary>Сколько столбцов чанков помнить (по 4 КБ). Больше — забываем все: посчитать заново дёшево.</summary>
    private const int MaxColumns = 4096;

    private readonly ConcurrentDictionary<(int X, int Z), int[]> _tops = new();

    /// <summary>Считает высоты столбца чанков заранее. Из любого потока: так делают потоки генерации.</summary>
    public void Prepare(int chunkX, int chunkZ) => TopsOf((chunkX, chunkZ));

    public bool IsOpen(int x, int z, int y) =>
        TopsOf((x >> ChunkMath.Bits, z >> ChunkMath.Bits))[(x & ChunkMath.Mask) + ((z & ChunkMath.Mask) << ChunkMath.Bits)] <= y;

    private int[] TopsOf((int X, int Z) key)
    {
        if (_tops.TryGetValue(key, out var tops))
            return tops;
        if (_tops.Count >= MaxColumns)
            _tops.Clear();
        // Два потока могут посчитать один столбец одновременно: результат одинаковый, лишняя работа редка.
        return _tops.GetOrAdd(key, static (column, terrain) => terrain.NaturalTops(column.X, column.Z), terrain);
    }
}
