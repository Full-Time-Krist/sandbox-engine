using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Мир на сервере. Пока фиксированного размера и целиком в памяти; подгрузка вокруг игрока — шаг 2.4.
/// Чанки генерируются в фоновых потоках и попадают в мир в тике сервера (<see cref="CollectGenerated"/>).
/// </summary>
public sealed class World : IDisposable
{
    /// <summary>
    /// Границы мира в чанках, включительно: 8×8 чанков по горизонтали с центром в начале координат,
    /// по высоте от −64 до +192 — весь диапазон рельефа.
    /// </summary>
    public static readonly ChunkPos MinChunk = new(-4, TerrainGenerator.MinSurface >> ChunkMath.Bits, -4);
    public static readonly ChunkPos MaxChunk = new(3, (TerrainGenerator.MaxSurface >> ChunkMath.Bits) - 1, 3);

    private readonly ChunkGenerator _generator;

    public ChunkMap Chunks { get; } = new();

    public int Seed { get; }

    /// <summary>Точка появления: над поверхностью в центре мира, с запасом для обзора.</summary>
    public Vector3 SpawnPosition { get; }

    /// <summary>Чанков ещё в генерации. 0 — мир готов целиком.</summary>
    public int PendingChunks => _generator.Pending;

    internal World(BaseBlocks blocks, int seed, int generatorThreads)
    {
        Seed = seed;
        var terrain = new TerrainGenerator(blocks, seed);
        SpawnPosition = new Vector3(0.5f, terrain.SurfaceHeight(0, 0) + 12f, 0.5f);
        _generator = new ChunkGenerator(terrain, generatorThreads);

        // Сначала ближние к точке появления: игрок увидит мир вокруг себя раньше дальних краёв.
        var center = BlockPos.Containing(SpawnPosition).Chunk;
        var positions = new List<ChunkPos>();
        for (var x = MinChunk.X; x <= MaxChunk.X; x++)
        for (var y = MinChunk.Y; y <= MaxChunk.Y; y++)
        for (var z = MinChunk.Z; z <= MaxChunk.Z; z++)
            positions.Add(new ChunkPos(x, y, z));

        foreach (var position in positions.OrderBy(p => DistanceSquared(p, center)))
            _generator.Request(position);
    }

    /// <summary>Число чанков в мире после окончания генерации.</summary>
    public static int TotalChunks =>
        (MaxChunk.X - MinChunk.X + 1) * (MaxChunk.Y - MinChunk.Y + 1) * (MaxChunk.Z - MinChunk.Z + 1);

    /// <summary>Переносит готовые чанки в мир и добавляет их в <paramref name="added"/>. Только из потока сервера.</summary>
    internal void CollectGenerated(List<Chunk> added)
    {
        while (_generator.TryTakeCompleted(out var chunk))
        {
            Chunks.Set(chunk);
            added.Add(chunk);
        }
    }

    private static int DistanceSquared(ChunkPos a, ChunkPos b)
    {
        int dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    public void Dispose() => _generator.Dispose();
}
