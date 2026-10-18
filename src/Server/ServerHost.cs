using System.Diagnostics;
using Shared;

namespace Server;

/// <summary>
/// Крутит <see cref="GameServer"/> в отдельном потоке с фиксированной частотой тиков.
/// Встроенный сервер одиночной игры: живёт, пока открыто окно, и не зависит от кадров клиента.
/// </summary>
/// <remarks>
/// С клиентом сервер общается только через потокобезопасные очереди <see cref="Shared.Networking.LocalConnection"/>,
/// поэтому всё состояние <see cref="GameServer"/> после <see cref="Start"/> принадлежит потоку сервера.
/// Снаружи можно только подключать клиентов (<see cref="GameServer.Connect"/>) и читать счётчики хоста.
/// </remarks>
public sealed class ServerHost : IDisposable
{
    /// <summary>
    /// Если сервер отстал сильнее (тик длился долго, сработал отладчик), он не догоняет пропущенные тики,
    /// а продолжает с текущего момента. То же правило, что у цикла клиента: иначе «спираль смерти».
    /// </summary>
    internal const double MaxLagSeconds = 0.25;

    /// <summary>Как часто писать в лог статистику тиков.</summary>
    private const double StatsIntervalSeconds = 10;

    private readonly Action _tick;
    private readonly double _tickDuration;
    private readonly bool _logStats;
    private readonly ManualResetEventSlim _stopRequested = new();
    private Thread? _thread;
    private long _ticks;

    /// <param name="logStats">Писать ли в лог статистику тиков (в тестах не нужно).</param>
    public ServerHost(GameServer server, int ticksPerSecond = GameTime.TicksPerSecond, bool logStats = true)
        : this(server.Tick, ticksPerSecond, logStats)
    {
    }

    /// <summary>Хост для произвольного тика: тесты подставляют сюда счётчик или падающий тик.</summary>
    internal ServerHost(Action tick, int ticksPerSecond, bool logStats)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _tick = tick;
        _tickDuration = 1.0 / ticksPerSecond;
        _logStats = logStats;
    }

    /// <summary>Сколько тиков выполнено с запуска. Можно читать из любого потока.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>
    /// Сколько секунд расписания пропущено без тиков: после отставания больше <see cref="MaxLagSeconds"/> сервер не
    /// догоняет пропущенное, а начинает отсчёт заново. Ожидаемое число тиков — по времени без этих секунд.
    /// </summary>
    public double SkippedSeconds => Volatile.Read(ref _skippedSeconds);
    private double _skippedSeconds;

    /// <summary>Работает ли поток сервера.</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// Исключение, на котором упал поток сервера, или null. Клиент проверяет его каждый тик:
    /// иначе после падения сервера игра продолжила бы рисовать замерший мир, ничего не сообщив.
    /// </summary>
    public Exception? Fault => _fault;

    // volatile: пишет поток сервера, читает поток клиента.
    private volatile Exception? _fault;

    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("Сервер уже запущен");

        // Фоновый поток не держит процесс: если клиент упадёт мимо Stop, игра всё равно завершится.
        _thread = new Thread(Run) { Name = "Server", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Просит поток завершиться после текущего тика и ждёт его. Повторный вызов ничего не делает.</summary>
    public void Stop()
    {
        if (_thread == null)
            return;

        _stopRequested.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            Console.WriteLine("[Server] Поток сервера не завершился за 5 с");
    }

    public void Dispose()
    {
        Stop();
        _stopRequested.Dispose();
    }

    private void Run()
    {
        try
        {
            Loop();
        }
        catch (Exception e)
        {
            // Исключение в потоке без обработчика завершило бы весь процесс без внятного сообщения.
            // Сохраняем его: клиент заметит Fault и остановится с ошибкой.
            _fault = e;
            Console.Error.WriteLine($"[Server] Сервер упал: {e}");
        }
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        var nextTick = 0.0;
        var stats = new TickStats(clock.Elapsed.TotalSeconds);

        while (!_stopRequested.IsSet)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now < nextTick)
            {
                // Ждём до следующего тика, но просыпаемся сразу, если попросили остановиться.
                // Время тика считается от начала отсчёта, а не от прошлого пробуждения: если ожидание
                // затянулось, следующее будет короче, и в среднем частота держится ровно.
                _stopRequested.Wait(TimeSpan.FromSeconds(nextTick - now));
                continue;
            }

            var tickStart = clock.Elapsed.TotalSeconds;
            _tick();
            var tickEnd = clock.Elapsed.TotalSeconds;
            Interlocked.Increment(ref _ticks);

            nextTick += _tickDuration;
            if (tickEnd - nextTick > MaxLagSeconds)
            {
                Console.WriteLine($"[Server] Отставание {(tickEnd - nextTick) * 1000:F0} мс, пропущенные тики не догоняем");
                Volatile.Write(ref _skippedSeconds, _skippedSeconds + (tickEnd - nextTick));
                nextTick = tickEnd;
            }

            stats.Record(tickStart, tickEnd);
            if (_logStats && tickEnd - stats.Since >= StatsIntervalSeconds)
            {
                Console.WriteLine(stats.Format(tickEnd));
                stats = new TickStats(tickEnd);
            }
        }
    }

    /// <summary>Статистика тиков за интервал: частота, длительность тика и самый большой промежуток между тиками.</summary>
    private sealed class TickStats(double since)
    {
        private int _count;
        private double _busy;
        private double _maxDuration;
        private double _maxInterval;
        private double _lastStart = double.NaN;

        public double Since { get; } = since;

        public void Record(double start, double end)
        {
            _count++;
            _busy += end - start;
            _maxDuration = Math.Max(_maxDuration, end - start);
            if (!double.IsNaN(_lastStart))
                _maxInterval = Math.Max(_maxInterval, start - _lastStart);
            _lastStart = start;
        }

        public string Format(double now) =>
            $"[Server] {now:F0} с: TPS {_count / (now - Since):F1}, тик в среднем {_busy / _count * 1000:F2} мс, " +
            $"максимум {_maxDuration * 1000:F2} мс, наибольший промежуток между тиками {_maxInterval * 1000:F0} мс";
    }
}
