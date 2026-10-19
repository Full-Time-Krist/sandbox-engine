namespace Shared.Voxels;

/// <summary>
/// Область чанков вокруг точки: цилиндр с радиусом <see cref="Horizontal"/> по горизонтали (круг, а не квадрат:
/// углы квадрата дальше дальности прорисовки, их всё равно не видно) и ±<see cref="Vertical"/> по высоте.
/// Сервер так выбирает, какие чанки слать игроку, а клиент (область приходит в <see cref="Protocol.JoinAccepted"/>) —
/// каких соседей чанка ждать не нужно: за областью они не придут.
/// </summary>
public readonly record struct ChunkArea(int Horizontal, int Vertical)
{
    public bool Contains(ChunkPos center, ChunkPos position)
    {
        int dx = position.X - center.X, dy = position.Y - center.Y, dz = position.Z - center.Z;
        return dx * dx + dz * dz <= Horizontal * Horizontal && Math.Abs(dy) <= Vertical;
    }

    /// <summary>
    /// Смещения всех чанков области от центра, от ближних к дальним. Считаются один раз:
    /// при переходе игрока в соседний чанк к ним просто прибавляется новый центр, без сортировки.
    /// </summary>
    public ChunkPos[] SortedOffsets()
    {
        var offsets = new List<ChunkPos>();
        for (var x = -Horizontal; x <= Horizontal; x++)
        for (var y = -Vertical; y <= Vertical; y++)
        for (var z = -Horizontal; z <= Horizontal; z++)
        {
            var offset = new ChunkPos(x, y, z);
            if (Contains(default, offset))
                offsets.Add(offset);
        }

        // По расстоянию в пространстве; при равном — сначала по горизонтали ближе, чтобы порядок был однозначным.
        return [.. offsets
            .OrderBy(o => o.X * o.X + o.Y * o.Y + o.Z * o.Z)
            .ThenBy(o => o.X * o.X + o.Z * o.Z)
            .ThenBy(o => o.Y).ThenBy(o => o.X).ThenBy(o => o.Z)];
    }
}
