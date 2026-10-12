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
        var seconds = clock.Elapsed.TotalSeconds;

        // Первый тик сразу при старте, дальше каждые 50 мс. Допуск — на задержки планировщика ОС.
        var expected = 1 + seconds * 20;
        Assert.InRange(ticks, expected - 3, expected + 1);
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
