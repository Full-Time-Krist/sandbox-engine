using Shared.Blocks;
using Shared.Lighting;
using Shared.Voxels;

namespace Client.Rendering;

/// <summary>
/// Двадцать шесть соседей чанка — куб 3 × 3 × 3 без середины: по граням, рёбрам и углам. Отсутствующий (null) сосед
/// для меша — воздух под полным небом. <c>default</c> — ни одного соседа.
/// </summary>
internal readonly struct ChunkNeighbors
{
    private readonly Chunk?[]? _chunks;

    private ChunkNeighbors(Chunk?[] chunks) => _chunks = chunks;

    /// <summary>Только соседи по граням (рёбра и углы — нет). Для тестов.</summary>
    public ChunkNeighbors(Chunk? negX, Chunk? posX, Chunk? negY, Chunk? posY, Chunk? negZ, Chunk? posZ)
    {
        _chunks = new Chunk?[27];
        _chunks[Slot(-1, 0, 0)] = negX;
        _chunks[Slot(1, 0, 0)] = posX;
        _chunks[Slot(0, -1, 0)] = negY;
        _chunks[Slot(0, 1, 0)] = posY;
        _chunks[Slot(0, 0, -1)] = negZ;
        _chunks[Slot(0, 0, 1)] = posZ;
    }

    /// <summary>Все соседи чанка в <paramref name="position"/>, какие есть в <paramref name="chunks"/>.</summary>
    public static ChunkNeighbors Around(ChunkMap chunks, ChunkPos position)
    {
        var around = new Chunk?[27];
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx != 0 || dy != 0 || dz != 0)
                around[Slot(dx, dy, dz)] = chunks.Get(new ChunkPos(position.X + dx, position.Y + dy, position.Z + dz));
        }
        return new ChunkNeighbors(around);
    }

    /// <summary>Сосед со смещением (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>), каждое от −1 до 1.</summary>
    public Chunk? this[int dx, int dy, int dz] => _chunks?[Slot(dx, dy, dz)];

    public Chunk? NegX => this[-1, 0, 0];
    public Chunk? PosX => this[1, 0, 0];
    public Chunk? NegY => this[0, -1, 0];
    public Chunk? PosY => this[0, 1, 0];
    public Chunk? NegZ => this[0, 0, -1];
    public Chunk? PosZ => this[0, 0, 1];

    private static int Slot(int dx, int dy, int dz) => (dx + 1) + (dy + 1) * 3 + (dz + 1) * 9;
}

/// <summary>
/// Неизменяемая копия блоков и света чанка с рамкой в один блок из соседей: всё, что нужно мешеру.
/// Мир клиента меняется только в основном потоке, поэтому фоновому построению меша нужен такой снимок,
/// а не сами чанки: иначе блок мог бы поменяться посреди построения.
/// </summary>
/// <remarks>
/// Массивы 34³: координаты от −1 до 32 по каждой оси. Внутренние 32³ — сам чанк, рамка — граничные блоки всех
/// 26 соседей, включая рёбра и углы: плавному свету и затенению углов нужны блоки по диагонали от грани.
/// Отсутствующий сосед — воздух под полным небом. Заодно мешеру не нужно проверять выход за чанк на каждом блоке.
/// </remarks>
internal sealed class ChunkSnapshot
{
    public const int Size = ChunkMath.Size + 2;
    public const int Volume = Size * Size * Size;

    /// <summary>Свет отсутствующего соседа: полное небо, без блочного.</summary>
    public static readonly byte MissingLight = ChunkLight.Pack(ChunkLight.Max, 0);

    private readonly BlockId[] _blocks = new BlockId[Volume];
    private readonly byte[] _light = new byte[Volume];

    public ChunkPos Position { get; private set; }

    /// <summary>Пуст ли сам чанк (рамка не считается): меш ему не нужен.</summary>
    public bool IsEmpty { get; private set; }

    /// <summary>Индекс в массиве снимка; координаты от −1 до 32.</summary>
    public static int Index(int x, int y, int z) => (x + 1) + (z + 1) * Size + (y + 1) * Size * Size;

    public BlockId this[int x, int y, int z] => _blocks[Index(x, y, z)];

    public ReadOnlySpan<BlockId> Blocks => _blocks;

    /// <summary>Упакованный свет (<see cref="ChunkLight.Get"/>) в порядке <see cref="Index"/>.</summary>
    public ReadOnlySpan<byte> Light => _light;

    /// <summary>
    /// Заполняет снимок. Объект переиспользуется (пул), но очищать его не нужно: у непустого чанка внутренность
    /// и вся рамка перезаписываются целиком, а пустой снимок мешер не читает.
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
            var to = Index(0, y, z);
            source.Slice(from, ChunkMath.Size).CopyTo(_blocks.AsSpan(to, ChunkMath.Size));
            chunk.Light.CopyTo(from, _light.AsSpan(to, ChunkMath.Size));
        }

        // Рамка: каждая клетка с координатой −1 или 32 хотя бы по одной оси берётся у соседа в той стороне
        // (−1 — его слой 31, 32 — его слой 0). Строка y, z целиком в рамке — это клетка, 32 подряд (строка соседа
        // вдоль X лежит в памяти подряд, копируется целиком) и ещё клетка; иначе рамка в строке — только два крайних x.
        for (var y = -1; y <= ChunkMath.Size; y++)
        for (var z = -1; z <= ChunkMath.Size; z++)
        {
            int sy = Side(y), sz = Side(z);
            CaptureFrame(-1, y, z, neighbors[-1, sy, sz], 1);
            if (sy != 0 || sz != 0)
                CaptureFrame(0, y, z, neighbors[0, sy, sz], ChunkMath.Size);
            CaptureFrame(ChunkMath.Size, y, z, neighbors[1, sy, sz], 1);
        }
    }

    /// <summary>Клетки рамки от (<paramref name="x"/>, y, z) вдоль X, <paramref name="count"/> штук — у соседа <paramref name="neighbor"/>.</summary>
    private void CaptureFrame(int x, int y, int z, Chunk? neighbor, int count)
    {
        var blocks = _blocks.AsSpan(Index(x, y, z), count);
        var light = _light.AsSpan(Index(x, y, z), count);
        if (neighbor is null)
        {
            blocks.Fill(BlockId.Air);
            light.Fill(MissingLight);
            return;
        }

        var from = new LocalPos(x & ChunkMath.Mask, y & ChunkMath.Mask, z & ChunkMath.Mask).Index;
        neighbor.Blocks.Slice(from, count).CopyTo(blocks);
        neighbor.Light.CopyTo(from, light);
    }

    private static int Side(int coordinate) => coordinate < 0 ? -1 : coordinate >= ChunkMath.Size ? 1 : 0;
}
