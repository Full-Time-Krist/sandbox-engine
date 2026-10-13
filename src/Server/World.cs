using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Мир на сервере: загруженные чанки, их загрузка и генерация. Мир бесконечный: чанки готовятся по запросу
/// (<see cref="TryRequest"/>) в фоновых потоках — читаются из сохранения или генерируются, попадают в мир
/// в тике сервера (<see cref="CollectGenerated"/>) и выгружаются, когда рядом нет игроков (<see cref="Unload"/>).
/// Все методы — только из потока сервера.
/// </summary>
/// <remarks>
/// Сохраняется каждый сгенерированный чанк: свежий — сразу в фоновом потоке, изменённый («грязный») — при выгрузке,
/// при автосохранении (<see cref="SaveDirty"/>) и при выходе. Генератор будет меняться по ходу разработки; если бы
/// хранились только изменённые чанки, нетронутые места пересоздавались бы по-новому и у построек появлялись бы обрывы.
/// </remarks>
public sealed class World : IDisposable
{
    /// <summary>
    /// Сколько чанков может генерироваться одновременно. Больше в очередь не ставим: пока игрок летит,
    /// важное меняется каждый тик, а длинная очередь генерировала бы то, что уже позади. Сервер каждый тик
    /// заново выбирает ближайшие недостающие чанки, поэтому порядок всегда актуальный.
    /// </summary>
    public const int MaxGenerating = 64;

    private readonly TerrainGenerator _terrain;
    private readonly WorldSave? _save;
    private readonly ChunkGenerator _generator;
    private readonly HashSet<ChunkPos> _generating = [];

    /// <summary>Чанки, изменённые после последней записи в сохранение.</summary>
    private readonly HashSet<ChunkPos> _dirty = [];

    private int _loadedCount;
    private int _generatedCount;

    public ChunkMap Chunks { get; } = new();

    public int Seed { get; }

    /// <summary>Точка появления — ноги игрока на поверхности (по умолчанию над началом координат).</summary>
    public Vector3d SpawnPosition { get; }

    /// <summary>Сколько чанков сейчас генерируется.</summary>
    public int GeneratingCount => _generating.Count;

    /// <summary>Сколько изменённых чанков ждёт записи в сохранение.</summary>
    public int DirtyCount => _dirty.Count;

    /// <summary>Сколько чанков с запуска прочитано из сохранения. Можно читать из любого потока.</summary>
    public int LoadedCount => Volatile.Read(ref _loadedCount);

    /// <summary>Сколько чанков с запуска сгенерировано заново. Можно читать из любого потока.</summary>
    public int GeneratedCount => Volatile.Read(ref _generatedCount);

    /// <param name="seed">Сид нового мира. Если мир уже есть в сохранении, сид и точка появления берутся оттуда.</param>
    /// <param name="spawnColumn">Столбец, над которым появляется игрок. Для проверок вдали от центра мира.</param>
    /// <param name="save">Сохранение; null — мир живёт только в памяти (тесты).</param>
    internal World(BaseBlocks blocks, int seed, int generatorThreads, (int X, int Z) spawnColumn = default, WorldSave? save = null)
    {
        _save = save;
        if (save?.Info is { } info)
        {
            Seed = info.Seed;
            _terrain = new TerrainGenerator(blocks, Seed);
            SpawnPosition = info.SpawnPosition;
        }
        else
        {
            Seed = seed;
            _terrain = new TerrainGenerator(blocks, Seed);
            var (sx, sz) = spawnColumn;
            // Позиция игрока — точка ног: стоит на верхнем блоке столбца. Тело (0,6) в середине столбца
            // не задевает соседние, даже если они выше.
            SpawnPosition = new Vector3d(sx + 0.5, _terrain.SurfaceHeight(sx, sz), sz + 0.5);
            save?.Initialize(new WorldInfo(Seed, SpawnPosition));
        }

        _generator = new ChunkGenerator(Produce, generatorThreads);
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

    /// <summary>Меняет блок и помечает чанк для сохранения. False — чанк не загружен.</summary>
    internal bool TrySetBlock(BlockPos pos, BlockId block)
    {
        if (!Chunks.TrySetBlock(pos, block))
            return false;

        _dirty.Add(pos.Chunk);
        return true;
    }

    /// <summary>Переносит готовые чанки в мир.</summary>
    internal void CollectGenerated()
    {
        // Запись идёт в своём потоке; если она сломалась, играть дальше значит терять изменения молча.
        _save?.ThrowIfFaulted();

        while (_generator.TryTakeCompleted(out var chunk))
        {
            _generating.Remove(chunk.Position);
            Chunks.Set(chunk);
        }
    }

    /// <summary>
    /// Удаляет чанки, для которых <paramref name="isNeeded"/> вернул false, изменённые перед этим сохраняет.
    /// Возвращает число удалённых.
    /// </summary>
    internal int Unload(Func<ChunkPos, bool> isNeeded)
    {
        var unneeded = Chunks.All.Where(c => !isNeeded(c.Position)).ToList();
        foreach (var chunk in unneeded)
        {
            if (_dirty.Remove(chunk.Position))
                _save?.SaveChunk(chunk);
            Chunks.Remove(chunk.Position);
        }
        return unneeded.Count;
    }

    /// <summary>Ставит в очередь записи все изменённые чанки. Возвращает их число.</summary>
    internal int SaveDirty()
    {
        if (_save == null)
            return 0;

        var count = _dirty.Count;
        foreach (var position in _dirty)
            _save.SaveChunk(Chunks.Get(position)!);
        _dirty.Clear();
        return count;
    }

    /// <summary>Готовит чанк в фоновом потоке: из сохранения, а если там его нет — генерирует и сразу сохраняет.</summary>
    private Chunk Produce(ChunkPos position)
    {
        if (_save != null && _save.TryLoadChunk(position, out var saved))
        {
            Interlocked.Increment(ref _loadedCount);
            return saved;
        }

        var chunk = _terrain.Generate(position);
        // Пока чанк не отдан серверу, его никто не меняет: сжимать можно прямо здесь, в фоновом потоке.
        _save?.SaveChunk(chunk);
        Interlocked.Increment(ref _generatedCount);
        return chunk;
    }

    /// <summary>Останавливает фоновые потоки. Изменённые чанки после этого ещё можно сохранить (<see cref="SaveDirty"/>).</summary>
    public void Dispose() => _generator.Dispose();
}
