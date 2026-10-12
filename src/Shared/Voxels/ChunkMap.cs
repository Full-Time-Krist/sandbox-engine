using Shared.Blocks;

namespace Shared.Voxels;

/// <summary>Набор загруженных чанков с доступом к блокам по мировым координатам.</summary>
public sealed class ChunkMap
{
    private readonly Dictionary<ChunkPos, Chunk> _chunks = new();

    public int Count => _chunks.Count;
    public IEnumerable<Chunk> All => _chunks.Values;

    /// <summary>Добавляет чанк или заменяет уже загруженный в той же позиции.</summary>
    public void Set(Chunk chunk) => _chunks[chunk.Position] = chunk;

    /// <summary>Удаляет чанк. Возвращает false, если его не было.</summary>
    public bool Remove(ChunkPos position) => _chunks.Remove(position);

    public Chunk? Get(ChunkPos position) => _chunks.GetValueOrDefault(position);

    public bool Contains(ChunkPos position) => _chunks.ContainsKey(position);

    /// <summary>Блок по мировым координатам. В незагруженном чанке — воздух.</summary>
    public BlockId GetBlock(BlockPos pos) => Get(pos.Chunk)?.Get(pos.Local) ?? BlockId.Air;

    /// <summary>Меняет блок. Возвращает false, если чанк не загружен: незагруженный мир менять нельзя.</summary>
    public bool TrySetBlock(BlockPos pos, BlockId block)
    {
        var chunk = Get(pos.Chunk);
        if (chunk is null)
            return false;

        chunk.Set(pos.Local, block);
        return true;
    }
}
