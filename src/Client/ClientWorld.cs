using System.Buffers;
using Client.Rendering;
using Shared.Blocks;
using Shared.Lighting;
using Shared.Voxels;

namespace Client;

/// <summary>
/// Копия мира на клиенте: чанки, полученные от сервера, и список чанков, чей меш нужно перестроить.
/// С OpenGL не связана, поэтому тестируется без окна.
/// </summary>
internal sealed class ClientWorld
{
    private readonly HashSet<ChunkPos> _dirty = [];

    public ChunkMap Chunks { get; } = new();

    public int DirtyCount => _dirty.Count;

    /// <summary>
    /// Какие чанки сервер шлёт вокруг чанка игрока (<see cref="Shared.Protocol.JoinAccepted"/>); null — пока неизвестно.
    /// Сосед за этой областью не придёт, и ждать его для меша незачем (<see cref="IsSurrounded"/>).
    /// </summary>
    public ChunkArea? LoadArea { get; set; }

    /// <summary>Чанк, где стоит игрок: центр <see cref="LoadArea"/>. Клиент обновляет его каждый тик.</summary>
    public ChunkPos Center { get; set; }

    public void AddChunk(Chunk chunk)
    {
        Chunks.Set(chunk);
        _dirty.Add(chunk.Position);

        // У соседей меняются граничные грани (раньше на месте нового чанка был «воздух»), а у соседей и по рёбрам
        // и углам — свет и затенение вершин у края: их меши читают крайние блоки этого чанка.
        MarkNeighbors(chunk.Position, AllNeighbors);
    }

    /// <summary>
    /// Удаляет чанк, ушедший далеко от игрока. Меш удалит рендер: чанк помечается «грязным», а его уже нет в мире.
    /// Соседей не перестраиваем: их грани в сторону удалённого чанка смотрят наружу, от игрока, и всё равно не видны.
    /// </summary>
    public void RemoveChunk(ChunkPos position)
    {
        if (Chunks.Remove(position))
            _dirty.Add(position);
    }

    /// <summary>
    /// Можно ли строить меш чанка: загружены все шесть соседей (или чанк пуст и меш ему не нужен).
    /// Без соседа граничные грани строились бы как у воздуха, а когда сосед придёт, меш пришлось бы строить заново.
    /// При подгрузке по одному чанку это давало бы до шести перестроек на чанк. Сервер поэтому грузит
    /// на кольцо больше дальности прорисовки: крайнее кольцо — только соседи для мешей.
    /// </summary>
    public bool CanMesh(ChunkPos position)
    {
        var chunk = Chunks.Get(position);
        if (chunk is null)
            return false;
        if (chunk.IsEmpty)
            return true;

        foreach (var neighbor in NeighborPositions(position))
        {
            if (!Chunks.Contains(neighbor))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Есть ли все 26 соседей чанка — по граням, рёбрам и углам, — какие могут прийти: сосед за областью загрузки
    /// (<see cref="LoadArea"/> вокруг <see cref="Center"/>) не придёт, его и не ждём. Тогда приход соседей меш уже
    /// не изменит, и строить его можно сразу (<see cref="ChunkRenderer.ReadyToMesh"/>). Пустому чанку меш не нужен,
    /// он «окружён» всегда. Если центр клиента на тик разошёлся с серверным, худшее — лишняя перестройка или
    /// ожидание <see cref="ChunkRenderer.SettleTime"/>.
    /// </summary>
    public bool IsSurrounded(ChunkPos position)
    {
        if (Chunks.Get(position) is not { } chunk)
            return false;
        if (chunk.IsEmpty)
            return true;

        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0 && dz == 0)
                continue;
            var neighbor = new ChunkPos(position.X + dx, position.Y + dy, position.Z + dz);
            if (!Chunks.Contains(neighbor) && (LoadArea is not { } area || area.Contains(Center, neighbor)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Меняет блок по сообщению сервера и помечает меш чанка для перестройки. Если блок у края чанка,
    /// перестраиваются и соседи, чья рамка меша его содержит: по грани (открывается или закрывается граничная
    /// грань), а у ребра и угла — и соседи по ребру и углу (затенение их вершин у края).
    /// </summary>
    public bool SetBlock(BlockPos pos, BlockId block)
    {
        if (!Chunks.TrySetBlock(pos, block))
            return false;

        _dirty.Add(pos.Chunk);
        MarkNeighbors(pos.Chunk, Touched(pos.Local.Index));
        return true;
    }

    /// <summary>
    /// Заменяет свет чанка присланным сервером (<see cref="LightCodec"/>). Клиент свет не считает. False — чанка нет.
    /// Меш чанка перестраивается, если свет изменился, а меши соседей — только тех, у чьей границы он изменился.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные света повреждены; свет чанка тогда не меняется.</exception>
    public bool SetLight(ChunkPos position, ReadOnlyMemory<byte> light)
    {
        if (Chunks.Get(position) is not { } chunk)
            return false;

        var fresh = new ChunkLight();
        LightCodec.Decode(light, fresh);
        var before = ArrayPool<byte>.Shared.Rent(ChunkMath.Volume);
        var after = ArrayPool<byte>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var old = before.AsSpan(0, ChunkMath.Volume);
            var now = after.AsSpan(0, ChunkMath.Volume);
            chunk.Light.CopyTo(old);
            fresh.CopyTo(now);

            // Какие соседи читают изменившиеся блоки: собираем по всем отличиям.
            var touched = 0;
            var changed = false;
            for (var start = 0; start < ChunkMath.Volume; start++)
            {
                start += old[start..].CommonPrefixLength(now[start..]);
                if (start >= ChunkMath.Volume)
                    break;
                changed = true;
                touched |= Touched(start);
            }
            if (!changed)
                return true;

            chunk.Light.CopyFrom(now);
            _dirty.Add(position);
            MarkNeighbors(position, touched);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(before);
            ArrayPool<byte>.Shared.Return(after);
        }
        return true;
    }

    /// <summary>Упакованный свет блока (<see cref="ChunkLight.Get"/>); чанка нет — полное небо, как у рамки меша.</summary>
    public byte LightAt(BlockPos pos) =>
        Chunks.Get(pos.Chunk) is { } chunk ? chunk.Light.Get(pos.Local.Index) : ChunkSnapshot.MissingLight;

    /// <summary>Все 26 соседей — биты <see cref="Bit"/>, кроме середины.</summary>
    private const int AllNeighbors = ((1 << 27) - 1) & ~(1 << 13);

    /// <summary>Бит соседа со смещением (dx, dy, dz) в масках соседей; середина (сам чанк) — бит 13.</summary>
    private static int Bit(int dx, int dy, int dz) => 1 << ((dx + 1) + (dy + 1) * 3 + (dz + 1) * 9);

    /// <summary>
    /// Соседи, в рамку меша которых попадает блок чанка с индексом <paramref name="index"/>: у края по каждой оси —
    /// сосед в ту сторону, и все сочетания этих сторон (блок в углу чанка — в рамке семи соседей).
    /// </summary>
    private static int Touched(int index)
    {
        var local = LocalPos.FromIndex(index);
        int ex = Edge(local.X), ey = Edge(local.Y), ez = Edge(local.Z);
        if (ex == 0 && ey == 0 && ez == 0)
            return 0;

        var mask = 0;
        foreach (var dx in (ReadOnlySpan<int>)[0, ex])
        foreach (var dy in (ReadOnlySpan<int>)[0, ey])
        foreach (var dz in (ReadOnlySpan<int>)[0, ez])
            mask |= Bit(dx, dy, dz);
        return mask & ~Bit(0, 0, 0);

        static int Edge(int c) => c == 0 ? -1 : c == ChunkMath.Mask ? 1 : 0;
    }

    private void MarkNeighbors(ChunkPos center, int mask)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if ((mask & Bit(dx, dy, dz)) == 0)
                continue;
            var position = new ChunkPos(center.X + dx, center.Y + dy, center.Z + dz);
            if (Chunks.Contains(position))
                _dirty.Add(position);
        }
    }

    /// <summary>Все 26 соседей чанка, какие загружены: для снимка меша.</summary>
    public ChunkNeighbors GetNeighbors(ChunkPos p) => ChunkNeighbors.Around(Chunks, p);

    /// <summary>Переносит позиции «грязных» чанков в <paramref name="output"/> и очищает список.</summary>
    public void TakeDirty(List<ChunkPos> output)
    {
        output.AddRange(_dirty);
        _dirty.Clear();
    }

    private static ChunkPos[] NeighborPositions(ChunkPos p) =>
    [
        p with { X = p.X - 1 }, p with { X = p.X + 1 },
        p with { Y = p.Y - 1 }, p with { Y = p.Y + 1 },
        p with { Z = p.Z - 1 }, p with { Z = p.Z + 1 },
    ];
}
