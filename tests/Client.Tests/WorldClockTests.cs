using Server;
using Shared.Lighting;
using Shared.Networking;
using Shared.Protocol;

namespace Client.Tests;

public class WorldClockTests
{
    [Fact]
    public void BeforeSync_ClockIsUnknown_AndDoesNotTick()
    {
        var clock = new WorldClock();
        clock.Tick();
        clock.Tick();

        Assert.False(clock.IsKnown);
        clock.Sync(100);
        Assert.Equal(100, clock.Now);
    }

    [Fact]
    public void BetweenSyncs_CountsTicks_SyncSetsExactly()
    {
        var clock = new WorldClock();
        clock.Sync(1000);
        for (var i = 0; i < 7; i++)
            clock.Tick();

        Assert.Equal(1007, clock.Now);
        Assert.Equal(1007.25, clock.At(0.25f));

        clock.Sync(5000);
        Assert.Equal(5000, clock.Now);
    }

    [Fact]
    public void ClientClock_PredictsEverySyncOfRealServer()
    {
        // Клиент тикает вместе с сервером и считает сам: к каждой сверке его часы уже показывают то же, что сервер.
        using var server = new GameServer(seed: 1, generatorThreads: 1, viewDistance: 1, verticalDistance: 1);
        var (connection, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        connection.Send(new JoinRequest("Tester"));
        var clock = new WorldClock();
        var checkedSyncs = 0;

        for (var tick = 0; tick < 2 * TimeSync.IntervalTicks + 5; tick++)
        {
            server.Tick();
            // Как ClientApp.OnTick: тик часов, потом сообщения тика.
            clock.Tick();
            while (connection.TryReceive(out var message))
            {
                switch (message)
                {
                    case JoinAccepted accepted:
                        clock.Sync(accepted.WorldTime);
                        break;
                    case TimeSync sync:
                        Assert.Equal(sync.WorldTime, clock.Now);
                        clock.Sync(sync.WorldTime);
                        checkedSyncs++;
                        break;
                }
            }
        }

        Assert.True(clock.IsKnown);
        Assert.Equal(2, checkedSyncs);
        Assert.True(DayCycle.IsDay(clock.Now));
    }
}
