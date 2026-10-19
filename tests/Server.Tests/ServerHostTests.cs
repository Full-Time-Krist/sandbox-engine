using System.Diagnostics;
using Shared.Networking;
using Shared.Protocol;

namespace Server.Tests;

public class ServerHostTests
{
    /// <summary>Ждёт условия не дольше таймаута. Тесты потоков не должны зависеть от точного времени.</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
                return false;
            Thread.Sleep(5);
        }
        return true;
    }

    [Fact]
    public void Start_TicksInOwnThread()
    {
        var tickThreads = new HashSet<int>();
        using var host = new ServerHost(() => { lock (tickThreads) tickThreads.Add(Environment.CurrentManagedThreadId); },
            ticksPerSecond: 100, logStats: false);

        host.Start();

        Assert.True(WaitUntil(() => host.Ticks >= 5), $"За 2 с выполнено {host.Ticks} тиков");
        host.Stop();
        // Все тики — в одном потоке, и это не поток теста.
        var thread = Assert.Single(tickThreads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
    }

    /// <summary>
    /// Частота — 20 тиков в секунду. Проверка поведением, без часов в потоке теста: начало каждого тика и пропущенное
    /// к этому моменту время записывает сам тик. Хост никогда не тикает раньше расписания — k-й тик начинается не раньше
    /// «старт + k × 50 мс + пропущенное» (граница из устройства, нагрузка тики только задерживает). Слишком редкие тики
    /// ловит медианное опоздание: при 10 тиках в секунду к двадцатому тику оно около 0,75 с, а у верного хоста опоздание
    /// больше MaxLagSeconds пропускается, меньшее догоняется. Медиана не боится отдельных задержек потока под нагрузкой
    /// (прошлая версия читала счётчик по часам теста и падала при полном прогоне под нагрузкой).
    /// </summary>
    [Fact]
    public void TickRate_MatchesTicksPerSecond()
    {
        const double Period = 1.0 / 20;
        const double Epsilon = 0.001;
        var ticks = new List<(double Start, double Skipped)>();
        var clock = Stopwatch.StartNew();
        ServerHost? host = null;
        host = new ServerHost(() => ticks.Add((clock.Elapsed.TotalSeconds, host!.SkippedSeconds)), ticksPerSecond: 20, logStats: false);
        using (host)
        {
            var hostStart = clock.Elapsed.TotalSeconds;
            host.Start();
            Assert.True(WaitUntil(() => host.Ticks >= 21, timeoutMs: 10_000), $"Выполнено {host.Ticks} тиков");
            host.Stop();

            var lags = new List<double>();
            for (var k = 0; k <= 20; k++)
            {
                var lag = ticks[k].Start - hostStart - ticks[k].Skipped - k * Period;
                Assert.True(lag >= -Epsilon, $"Тик {k} начался на {-lag * 1000:F1} мс раньше расписания");
                if (k > 0)
                    lags.Add(lag);
            }
            lags.Sort();
            Assert.True(lags[lags.Count / 2] < ServerHost.MaxLagSeconds, $"Медианное опоздание {lags[lags.Count / 2] * 1000:F0} мс: тики реже положенного");
        }
    }

    /// <summary>
    /// Долгий тик (дольше MaxLagSeconds) не догоняется пачкой тиков: отставание пропускается и учитывается в SkippedSeconds,
    /// а расписание начинается заново от конца долгого тика.
    /// </summary>
    /// <remarks>
    /// Проверка — поведением, без допусков на нагрузку. Хост никогда не тикает раньше расписания (ждёт, пока не наступит
    /// время тика), а нагрузка может тики только задержать. Поэтому k-й тик после долгого начинается не раньше
    /// «конец долгого + (k − 1) × 50 мс». Если бы хост догонял пропущенное, расписание осталось бы от старта, и около
    /// двадцати тиков пошли бы сразу, без пауз. Прошлая версия считала тики по часам и падала под нагрузкой
    /// параллельных тестов: опоздание меньше MaxLagSeconds догоняется позже, и в момент чтения тиков не хватало.
    /// </remarks>
    [Fact]
    public void LongTick_IsSkippedNotCaughtUp_AndCounted()
    {
        const double Period = 1.0 / 20;
        const double Epsilon = 0.001;
        var starts = new List<double>();
        var longEnd = 0.0;
        var skippedAfterLong = double.NaN;
        var clock = Stopwatch.StartNew();
        ServerHost? host = null;
        host = new ServerHost(() =>
        {
            starts.Add(clock.Elapsed.TotalSeconds);
            // Пропущенное — в первом тике после долгого: под нагрузкой хост может отстать и позже, и это тоже копится.
            if (starts.Count == 2)
                skippedAfterLong = host!.SkippedSeconds;
            if (starts.Count != 1)
                return;
            Thread.Sleep(1000);
            longEnd = clock.Elapsed.TotalSeconds;
        }, ticksPerSecond: 20, logStats: false);
        using var disposeHost = host;
        var hostStart = clock.Elapsed.TotalSeconds;
        host.Start();

        Assert.True(WaitUntil(() => host.Ticks >= 8, timeoutMs: 10_000), $"Выполнено {host.Ticks} тиков");
        host.Stop();

        // Тики после долгого: не раньше расписания, отсчитанного от его конца.
        for (var k = 1; k < starts.Count; k++)
        {
            var earliest = longEnd + (k - 1) * Period;
            Assert.True(starts[k] >= earliest - Epsilon,
                $"Тик {k} после долгого начался в {starts[k]:F4} с, раньше расписания {earliest:F4} с: пропущенное догонялось");
        }

        // Пропущено «конец долгого тика − время следующего по расписанию (50 мс от старта)». Начало отсчёта хоста —
        // между вызовом Start и первым тиком, конец долгого тика по часам хоста — между longEnd и началом следующего.
        Assert.InRange(skippedAfterLong, longEnd - starts[0] - Period - Epsilon, starts[1] - hostStart - Period + Epsilon);
    }

    [Fact]
    public void Stop_WakesThreadImmediately_AndTicksStop()
    {
        // Один тик в секунду: если Stop ждал бы конца ожидания, остановка заняла бы почти секунду.
        using var host = new ServerHost(() => { }, ticksPerSecond: 1, logStats: false);
        host.Start();
        Assert.True(WaitUntil(() => host.Ticks >= 1));

        var clock = Stopwatch.StartNew();
        host.Stop();

        Assert.True(clock.ElapsedMilliseconds < 500, $"Остановка заняла {clock.ElapsedMilliseconds} мс");
        Assert.False(host.IsRunning);
        var ticks = host.Ticks;
        Thread.Sleep(1100);
        Assert.Equal(ticks, host.Ticks);
    }

    [Fact]
    public void Stop_IsSafeBeforeStartAndTwice()
    {
        using var host = new ServerHost(() => { }, ticksPerSecond: 20, logStats: false);
        host.Stop();

        host.Start();
        host.Stop();
        host.Stop();

        Assert.False(host.IsRunning);
    }

    [Fact]
    public void ExceptionInTick_IsStoredInFault_AndThreadEnds()
    {
        using var host = new ServerHost(() => throw new InvalidOperationException("сбой в тике"), ticksPerSecond: 20, logStats: false);
        host.Start();

        Assert.True(WaitUntil(() => host.Fault != null));
        Assert.Equal("сбой в тике", host.Fault!.Message);
        Assert.True(WaitUntil(() => !host.IsRunning));
    }

    [Fact]
    public void ClientConnectedWhileRunning_IsServedWithoutManualTicks()
    {
        using var server = TestServers.Create();
        using var host = new ServerHost(server, logStats: false);
        host.Start();

        // Подключение из потока теста, пока сервер уже тикает в своём.
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        client.Send(new JoinRequest("Tester"));

        ServerMessage? first = null;
        Assert.True(WaitUntil(() => client.TryReceive(out first)), "Сервер не ответил на JoinRequest");
        Assert.IsType<JoinAccepted>(first);
    }
}
