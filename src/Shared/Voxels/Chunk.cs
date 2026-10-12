using Shared.Blocks;

namespace Shared.Voxels;

/// <summary>Куб из 32³ блоков. Хранит только числовые id; что они значат, знает <see cref="BlockRegistry"/>.</summary>
public sealed class Chunk(ChunkPos position)
{
    // Новый массив заполнен нулями, а 0 — это воздух.
    private readonly BlockId[] _blocks = new BlockId[ChunkMath.Volume];

    public ChunkPos Position { get; } = position;

    /// <summary>Число блоков, отличных от воздуха. Пустые чанки не нужно ни отрисовывать, ни передавать.</summary>
    public int NonAirCount { get; private set; }

    public bool IsEmpty => NonAirCount == 0;

    /// <summary>Все блоки в порядке <see cref="LocalPos.Index"/>, для построения меша и сериализации.</summary>
    public ReadOnlySpan<BlockId> Blocks => _blocks;

    /// <summary>Создаёт чанк из готового массива блоков (копирует его), например из сообщения сервера.</summary>
    public static Chunk FromBlocks(ChunkPos position, ReadOnlySpan<BlockId> blocks)
    {
        if (blocks.Length != ChunkMath.Volume)
            throw new ArgumentException($"Ожидалось {ChunkMath.Volume} блоков, получено {blocks.Length}.", nameof(blocks));

        var chunk = new Chunk(position);
        blocks.CopyTo(chunk._blocks);

        var nonAir = 0;
        foreach (var block in blocks)
        {
            if (!block.IsAir)
                nonAir++;
        }

        chunk.NonAirCount = nonAir;
        return chunk;
    }

    public BlockId Get(LocalPos pos) => _blocks[CheckedIndex(pos)];

    public void Set(LocalPos pos, BlockId block)
    {
        ref var slot = ref _blocks[CheckedIndex(pos)];

        if (slot.IsAir && !block.IsAir)
            NonAirCount++;
        else if (!slot.IsAir && block.IsAir)
            NonAirCount--;

        slot = block;
    }

    // Без проверки выход за границу не всегда даёт исключение: (32, 0, 0) превращается в индекс 32,
    // то есть в блок (0, 0, 1), и ошибка тихо портит мир.
    private static int CheckedIndex(LocalPos pos) =>
        pos.IsInBounds ? pos.Index : throw new ArgumentOutOfRangeException(nameof(pos), pos, "Позиция за пределами чанка.");
}
