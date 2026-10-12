using System.Collections.Concurrent;
using System.Diagnostics;
using Shared.Voxels;

namespace Client.Rendering;

/// <summary>Готовый меш из фонового потока. <see cref="Version"/> — чтобы отбросить устаревший результат.</summary>
internal sealed record MeshBuildResult(ChunkPos Position, int Version, ChunkMeshData Data, double BuildMs);

/// <summary>
/// Строит меши чанков в фоновых потоках по снимкам (<see cref="ChunkSnapshot"/>). Основной поток ставит снимки
/// в очередь (<see cref="Submit"/>) и забирает готовые меши (<see cref="TryTakeCompleted"/>), чтобы загрузить
/// их в видеопамять: OpenGL доступен только из потока окна.
/// </summary>
/// <remarks>
/// Снимки и массивы вершин переиспользуются через пулы: при полёте строятся сотни мешей в секунду,
/// и без пулов это были бы мегабайты мусора каждую секунду.
/// </remarks>
internal sealed class MeshBuildQueue : IDisposable
{
    private readonly ChunkMesher _mesher;
    private readonly BlockingCollection<(ChunkSnapshot Snapshot, int Version)> _jobs = new(new ConcurrentQueue<(ChunkSnapshot, int)>());
    private readonly ConcurrentQueue<MeshBuildResult> _completed = new();
    private readonly ConcurrentBag<ChunkSnapshot> _snapshotPool = [];
    private readonly ConcurrentBag<ChunkMeshData> _dataPool = [];
    private readonly Thread[] _workers;
    private volatile Exception? _fault;

    public MeshBuildQueue(ChunkMesher mesher, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        _mesher = mesher;
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(Work) { Name = $"Mesher-{i + 1}", IsBackground = true };
            _workers[i].Start();
        }
    }

    /// <summary>Потоки для мешей: остальные заняты окном, сервером и генерацией чанков.</summary>
    public static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>Снимок из пула. Его нужно заполнить и отдать в <see cref="Submit"/>.</summary>
    public ChunkSnapshot RentSnapshot() => _snapshotPool.TryTake(out var snapshot) ? snapshot : new ChunkSnapshot();

    public void Submit(ChunkSnapshot snapshot, int version) => _jobs.Add((snapshot, version));

    /// <summary>Готовый меш. После загрузки в видеопамять его данные нужно вернуть через <see cref="Return"/>.</summary>
    public bool TryTakeCompleted(out MeshBuildResult result)
    {
        if (_fault is { } fault)
            throw new InvalidOperationException("Ошибка построения меша", fault);
        return _completed.TryDequeue(out result!);
    }

    public void Return(ChunkMeshData data) => _dataPool.Add(data);

    private void Work()
    {
        try
        {
            foreach (var (snapshot, version) in _jobs.GetConsumingEnumerable())
            {
                var data = _dataPool.TryTake(out var pooled) ? pooled : new ChunkMeshData();
                var started = Stopwatch.GetTimestamp();
                _mesher.Build(snapshot, data);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                _completed.Enqueue(new MeshBuildResult(snapshot.Position, version, data, ms));
                _snapshotPool.Add(snapshot);
            }
        }
        catch (Exception e)
        {
            _fault = e;
        }
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        while (_jobs.TryTake(out _)) { }
        foreach (var worker in _workers)
            worker.Join();
        _jobs.Dispose();
    }
}
