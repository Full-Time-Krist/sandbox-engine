using Shared.Lighting;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Солнце над незагруженными чанками на сервере: открыто, если выше естественного рельефа и деревьев
/// (<see cref="TerrainGenerator.NaturalTops"/>). Постройки игроков в незагруженных чанках не учитываются — их без
/// чанка не видно (открытый вопрос этапа 8). Только из потока сервера.
/// </summary>
internal sealed class NaturalSky(TerrainGenerator terrain) : ISkyAbove
{
    /// <summary>Сколько столбцов чанков помнить (по 4 КБ). Больше — забываем все: посчитать заново дёшево.</summary>
    private const int MaxColumns = 4096;

    private readonly Dictionary<(int X, int Z), int[]> _tops = new();

    public bool IsOpen(int x, int z, int y)
    {
        var key = (x >> ChunkMath.Bits, z >> ChunkMath.Bits);
        if (!_tops.TryGetValue(key, out var tops))
        {
            if (_tops.Count >= MaxColumns)
                _tops.Clear();
            tops = terrain.NaturalTops(key.Item1, key.Item2);
            _tops.Add(key, tops);
        }
        return tops[(x & ChunkMath.Mask) + ((z & ChunkMath.Mask) << ChunkMath.Bits)] <= y;
    }
}
