using System.Collections.Concurrent;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Генерирует чанки в фоновых потоках. Поток сервера ставит позиции в очередь (<see cref="Request"/>)
/// и в своём тике забирает готовые чанки (<see cref="TryTakeCompleted"/>).
/// </summary>
/// <remarks>
/// Генерация чанка у поверхности — около миллисекунды, а при входе в мир их нужны сотни: в тике сервера
/// это остановило бы мир на секунды. Готовые чанки не попадают в мир сами: мир меняет только поток сервера,
/// поэтому фоновым потокам не нужны блокировки на данных мира.
/// Очередь — в порядке запросов: кто запрашивает, тот и решает, что важнее (ближние чанки — первыми).
/// </remarks>
internal sealed class ChunkGenerator : IDisposable
{
    private readonly TerrainGenerator _terrain;
    private readonly BlockingCollection<ChunkPos> _requests = new(new ConcurrentQueue<ChunkPos>());
    private readonly ConcurrentQueue<Chunk> _completed = new();
    private readonly Thread[] _workers;
    private volatile Exception? _fault;
    private int _pending;

    public ChunkGenerator(TerrainGenerator terrain, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        _terrain = terrain;
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            // Фоновые потоки не держат процесс, если Dispose не вызвали.
            _workers[i] = new Thread(Work) { Name = $"ChunkGen-{i + 1}", IsBackground = true };
            _workers[i].Start();
        }
    }

    /// <summary>Сколько потоков стоит отдать генерации на этой машине: остальные нужны клиенту, серверу и мешам.</summary>
    public static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>Сколько чанков запрошено, но ещё не забрано через <see cref="TryTakeCompleted"/>.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Request(ChunkPos position)
    {
        Interlocked.Increment(ref _pending);
        _requests.Add(position);
    }

    /// <summary>Достаёт следующий готовый чанк. Если фоновый поток упал, бросает его исключение.</summary>
    public bool TryTakeCompleted(out Chunk chunk)
    {
        if (_fault is { } fault)
            throw new InvalidOperationException("Ошибка генерации чанка", fault);

        if (!_completed.TryDequeue(out chunk!))
            return false;

        Interlocked.Decrement(ref _pending);
        return true;
    }

    private void Work()
    {
        try
        {
            // Цикл завершается, когда Dispose закрывает очередь (CompleteAdding).
            foreach (var position in _requests.GetConsumingEnumerable())
                _completed.Enqueue(_terrain.Generate(position));
        }
        catch (Exception e)
        {
            // Чанк, на котором упал генератор, никогда не придёт. Сообщаем серверу, а не ждём молча.
            _fault = e;
        }
    }

    public void Dispose()
    {
        // Оставшиеся запросы потоки уже не возьмут: GetConsumingEnumerable доработает очередь до конца,
        // поэтому сначала забираем всё, что не начато.
        _requests.CompleteAdding();
        while (_requests.TryTake(out _)) { }

        foreach (var worker in _workers)
            worker.Join();
        _requests.Dispose();
    }
}
