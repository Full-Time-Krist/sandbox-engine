using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Мир на сервере: загруженные чанки и их генерация. Мир бесконечный: чанки генерируются по запросу
/// (<see cref="TryRequest"/>) в фоновых потоках, попадают в мир в тике сервера (<see cref="CollectGenerated"/>)
/// и выгружаются, когда рядом нет игроков (<see cref="Unload"/>). Все методы — только из потока сервера.
/// </summary>
public sealed class World : IDisposable
{
    /// <summary>
    /// Сколько чанков может генерироваться одновременно. Больше в очередь не ставим: пока игрок летит,
    /// важное меняется каждый тик, а длинная очередь генерировала бы то, что уже позади. Сервер каждый тик
    /// заново выбирает ближайшие недостающие чанки, поэтому порядок всегда актуальный.
    /// </summary>
    public const int MaxGenerating = 64;

    private readonly ChunkGenerator _generator;
    private readonly HashSet<ChunkPos> _generating = [];

    public ChunkMap Chunks { get; } = new();

    public int Seed { get; }

    /// <summary>Точка появления: над поверхностью в начале координат, с запасом для обзора.</summary>
    public Vector3 SpawnPosition { get; }

    /// <summary>Сколько чанков сейчас генерируется.</summary>
    public int GeneratingCount => _generating.Count;

    internal World(BaseBlocks blocks, int seed, int generatorThreads)
    {
        Seed = seed;
        var terrain = new TerrainGenerator(blocks, seed);
        SpawnPosition = new Vector3(0.5f, terrain.SurfaceHeight(0, 0) + 12f, 0.5f);
        _generator = new ChunkGenerator(terrain, generatorThreads);
    }

    public bool IsGenerating(ChunkPos position) => _generating.Contains(position);

    /// <summary>
    /// Ставит чанк в генерацию, если его нет в мире и он ещё не генерируется.
    /// Возвращает false, если очередь заполнена (<see cref="MaxGenerating"/>): тогда дальше в этом тике просить незачем.
    /// </summary>
    public bool TryRequest(ChunkPos position)
    {
        if (Chunks.Contains(position) || _generating.Contains(position))
            return true;
        if (_generating.Count >= MaxGenerating)
            return false;

        _generating.Add(position);
        _generator.Request(position);
        return true;
    }

    /// <summary>Переносит готовые чанки в мир.</summary>
    internal void CollectGenerated()
    {
        while (_generator.TryTakeCompleted(out var chunk))
        {
            _generating.Remove(chunk.Position);
            Chunks.Set(chunk);
        }
    }

    /// <summary>
    /// Удаляет чанки, для которых <paramref name="isNeeded"/> вернул false. Возвращает число удалённых.
    /// Изменения в выгруженных чанках пока теряются: сохранение на диск — шаг 2.7.
    /// </summary>
    internal int Unload(Func<ChunkPos, bool> isNeeded)
    {
        var unneeded = Chunks.All.Select(c => c.Position).Where(p => !isNeeded(p)).ToList();
        foreach (var position in unneeded)
            Chunks.Remove(position);
        return unneeded.Count;
    }

    public void Dispose() => _generator.Dispose();
}
