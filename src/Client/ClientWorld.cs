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

    public void AddChunk(Chunk chunk)
    {
        Chunks.Set(chunk);
        _dirty.Add(chunk.Position);

        // У соседей меняются граничные грани: раньше на месте нового чанка был «воздух».
        foreach (var neighbor in NeighborPositions(chunk.Position))
            MarkIfLoaded(neighbor);
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
    /// Меняет блок по сообщению сервера и помечает меш чанка для перестройки. Если блок на границе чанка,
    /// перестраивается и сосед за этой границей: у него открывается или закрывается граничная грань.
    /// </summary>
    public bool SetBlock(BlockPos pos, BlockId block)
    {
        if (!Chunks.TrySetBlock(pos, block))
            return false;

        var chunk = pos.Chunk;
        var local = pos.Local;
        _dirty.Add(chunk);

        if (local.X == 0) MarkIfLoaded(chunk with { X = chunk.X - 1 });
        if (local.X == ChunkMath.Mask) MarkIfLoaded(chunk with { X = chunk.X + 1 });
        if (local.Y == 0) MarkIfLoaded(chunk with { Y = chunk.Y - 1 });
        if (local.Y == ChunkMath.Mask) MarkIfLoaded(chunk with { Y = chunk.Y + 1 });
        if (local.Z == 0) MarkIfLoaded(chunk with { Z = chunk.Z - 1 });
        if (local.Z == ChunkMath.Mask) MarkIfLoaded(chunk with { Z = chunk.Z + 1 });
        return true;
    }

    /// <summary>
    /// Заменяет свет чанка присланным сервером (<see cref="LightCodec"/>). Клиент свет не считает. False — чанка нет.
    /// Меши от света пока не зависят (он ещё не рисуется), поэтому не перестраиваются.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные света повреждены; свет чанка тогда не меняется.</exception>
    public bool SetLight(ChunkPos position, ReadOnlyMemory<byte> light)
    {
        if (Chunks.Get(position) is not { } chunk)
            return false;
        LightCodec.Decode(light, chunk.Light);
        return true;
    }

    private void MarkIfLoaded(ChunkPos position)
    {
        if (Chunks.Contains(position))
            _dirty.Add(position);
    }

    public ChunkNeighbors GetNeighbors(ChunkPos p) => new(
        Chunks.Get(p with { X = p.X - 1 }), Chunks.Get(p with { X = p.X + 1 }),
        Chunks.Get(p with { Y = p.Y - 1 }), Chunks.Get(p with { Y = p.Y + 1 }),
        Chunks.Get(p with { Z = p.Z - 1 }), Chunks.Get(p with { Z = p.Z + 1 }));

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
