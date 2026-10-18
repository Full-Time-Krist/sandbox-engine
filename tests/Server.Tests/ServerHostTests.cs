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

    [Fact]
    public void TickRate_MatchesTicksPerSecond()
    {
        using var host = new ServerHost(() => { }, ticksPerSecond: 20, logStats: false);
        var clock = Stopwatch.StartNew();
        host.Start();

        Thread.Sleep(1000);
        var ticks = host.Ticks;
        var seconds = clock.Elapsed.TotalSeconds - host.SkippedSeconds;

        // Первый тик сразу при старте, дальше каждые 50 мс. Под нагрузкой (параллельные тесты занимают все ядра)
        // сервер по своим правилам отстаёт: отставание больше MaxLagSeconds пропускается — это время не считаем
        // (SkippedSeconds), а меньшее догоняется потом, так что в момент чтения может не хватать до MaxLagSeconds × 20
        // тиков и ещё одного. Это граница из устройства ServerHost, а не подобранный допуск; частоту 10 вместо 20 она ловит.
        var expected = 1 + seconds * 20;
        Assert.InRange(ticks, expected - (ServerHost.MaxLagSeconds * 20 + 1), expected + 1);
    }

    /// <summary>
    /// Долгий тик (дольше MaxLagSeconds) не догоняется пачкой тиков: отставание пропускается и учитывается в SkippedSeconds,
    /// а дальше тики снова идут раз в 50 мс.
    /// </summary>
    [Fact]
    public void LongTick_IsSkippedNotCaughtUp_AndCounted()
    {
        var first = true;
        using var host = new ServerHost(() =>
        {
            if (!first)
                return;
            first = false;
            Thread.Sleep(1000);
        }, ticksPerSecond: 20, logStats: false);
        var clock = Stopwatch.StartNew();
        host.Start();

        Assert.True(WaitUntil(() => host.Ticks >= 2, timeoutMs: 5000));
        // Первый тик начался в 0 и закончился около 1 с; следующий по расписанию — в 0,05 с: пропущено около 0,95 с.
        Assert.InRange(host.SkippedSeconds, 0.85, 0.95 + clock.Elapsed.TotalSeconds - 1.0 + 0.1);

        Thread.Sleep(500);
        var ticks = host.Ticks;
        var seconds = clock.Elapsed.TotalSeconds - host.SkippedSeconds;
        // Без пропуска здесь было бы около 31 тика сразу после долгого (догоняли бы); с пропуском — по времени без него.
        Assert.InRange(ticks, 1 + seconds * 20 - 3, 1 + seconds * 20 + 1);
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
