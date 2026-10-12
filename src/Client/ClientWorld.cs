using Client.Rendering;
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
        {
            if (Chunks.Contains(neighbor))
                _dirty.Add(neighbor);
        }
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
