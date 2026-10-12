using Shared.Blocks;
using Shared.Voxels;

namespace Client.Rendering;

/// <summary>
/// Неизменяемая копия блоков чанка с рамкой в один блок из соседей: всё, что нужно мешеру.
/// Мир клиента меняется только в основном потоке, поэтому фоновому построению меша нужен такой снимок,
/// а не сами чанки: иначе блок мог бы поменяться посреди построения.
/// </summary>
/// <remarks>
/// Массив 34³: координаты от −1 до 32 по каждой оси. Внутренние 32³ — сам чанк, крайние слои — граничные
/// блоки шести соседей (отсутствующий сосед — воздух). Рёбра и углы рамки не заполняются: для отсечения граней
/// нужны только соседи по граням. Заодно мешеру не нужно проверять выход за чанк на каждом блоке.
/// </remarks>
internal sealed class ChunkSnapshot
{
    public const int Size = ChunkMath.Size + 2;
    public const int Volume = Size * Size * Size;

    private readonly BlockId[] _blocks = new BlockId[Volume];

    public ChunkPos Position { get; private set; }

    /// <summary>Пуст ли сам чанк (рамка не считается): меш ему не нужен.</summary>
    public bool IsEmpty { get; private set; }

    /// <summary>Индекс в массиве снимка; координаты от −1 до 32.</summary>
    public static int Index(int x, int y, int z) => (x + 1) + (z + 1) * Size + (y + 1) * Size * Size;

    public BlockId this[int x, int y, int z] => _blocks[Index(x, y, z)];

    public ReadOnlySpan<BlockId> Blocks => _blocks;

    /// <summary>
    /// Заполняет снимок. Объект переиспользуется (пул), но очищать его не нужно: у непустого чанка внутренность
    /// и шесть граней рамки перезаписываются целиком, рёбра и углы рамки не пишутся никогда (всегда воздух),
    /// а пустой снимок мешер не читает.
    /// </summary>
    public void Capture(Chunk chunk, in ChunkNeighbors neighbors)
    {
        Position = chunk.Position;
        IsEmpty = chunk.IsEmpty;
        if (IsEmpty)
            return;

        // Чанк: строки по X лежат подряд и в чанке, и в снимке, копируем их целиком.
        var source = chunk.Blocks;
        for (var y = 0; y < ChunkMath.Size; y++)
        for (var z = 0; z < ChunkMath.Size; z++)
        {
            var from = new LocalPos(0, y, z).Index;
            source.Slice(from, ChunkMath.Size).CopyTo(_blocks.AsSpan(Index(0, y, z), ChunkMath.Size));
        }

        const int Last = ChunkMath.Size - 1;
        for (var a = 0; a < ChunkMath.Size; a++)
        for (var b = 0; b < ChunkMath.Size; b++)
        {
            // Слой соседа, прилегающий к чанку: у соседа по −X это его X = 31, у соседа по +X — X = 0 и т. д.
            _blocks[Index(-1, a, b)] = Get(neighbors.NegX, Last, a, b);
            _blocks[Index(ChunkMath.Size, a, b)] = Get(neighbors.PosX, 0, a, b);
            _blocks[Index(a, -1, b)] = Get(neighbors.NegY, a, Last, b);
            _blocks[Index(a, ChunkMath.Size, b)] = Get(neighbors.PosY, a, 0, b);
            _blocks[Index(a, b, -1)] = Get(neighbors.NegZ, a, b, Last);
            _blocks[Index(a, b, ChunkMath.Size)] = Get(neighbors.PosZ, a, b, 0);
        }
    }

    private static BlockId Get(Chunk? chunk, int x, int y, int z) =>
        chunk is null || chunk.IsEmpty ? BlockId.Air : chunk.Get(new LocalPos(x, y, z));
}
