using Microsoft.Data.Sqlite;
using Server.Tests.Storage;
using Shared.Lighting;
using Shared.Networking;
using Shared.Protocol;

namespace Server.Tests;

/// <summary>Часы мира на сервере: тик в тик, сверки клиентам, перевод времени, сохранение.</summary>
public sealed class WorldTimeTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static List<ServerMessage> Drain(IConnection<ClientMessage, ServerMessage> client)
    {
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    [Fact]
    public void NewWorld_StartsInMorning_AndClockAdvancesOneTickPerTick()
    {
        using var server = TestServers.Create();
        var messages = new List<ServerMessage>();
        TestServers.JoinAndLoad(server, messages);
        var joined = Assert.IsType<JoinAccepted>(messages[0]);

        // Время входа — утро нового мира плюс тики до входа.
        Assert.InRange(joined.WorldTime, DayCycle.NewWorldTime + 1, DayCycle.NewWorldTime + 1000);
        var before = server.WorldTime;
        TestServers.Tick(server, 37);
        Assert.Equal(before + 37, server.WorldTime);
    }

    [Fact]
    public void TimeSync_EveryTenSeconds_CarriesServerTimeOfThatTick()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        Drain(client);

        var syncs = new List<(int Tick, long Time, long ServerTime)>();
        for (var tick = 0; tick < 3 * TimeSync.IntervalTicks; tick++)
        {
            server.Tick();
            foreach (var sync in Drain(client).OfType<TimeSync>())
                syncs.Add((tick, sync.WorldTime, server.WorldTime));
        }

        Assert.Equal(3, syncs.Count);
        Assert.All(syncs, s => Assert.Equal(s.ServerTime, s.Time));
        Assert.Equal(TimeSync.IntervalTicks, syncs[1].Tick - syncs[0].Tick);
        Assert.Equal(TimeSync.IntervalTicks, syncs[1].Time - syncs[0].Time);
    }

    [Fact]
    public void AdvanceTime_AddsHour_AndSyncsEveryPlayerInSameTick()
    {
        using var server = TestServers.Create();
        var first = TestServers.JoinAndLoad(server);
        var second = TestServers.JoinAndLoad(server);
        server.Tick();
        Drain(first);
        Drain(second);
        var before = server.WorldTime;

        first.Send(new AdvanceTime());
        server.Tick();

        // Тик сервера (+1) и «час» (+1/24 суток).
        Assert.Equal(before + 1 + DayCycle.HourTicks, server.WorldTime);
        Assert.Contains(new TimeSync(server.WorldTime), Drain(first));
        Assert.Contains(new TimeSync(server.WorldTime), Drain(second));
    }

    [Fact]
    public void Time_SurvivesRestart()
    {
        long saved;
        using (var server = TestServers.Create(savePath: _directory.SaveFile))
        {
            var client = TestServers.JoinAndLoad(server);
            client.Send(new AdvanceTime());
            TestServers.Tick(server, 5);
            saved = server.WorldTime;
        }

        using var restarted = TestServers.Create(savePath: _directory.SaveFile);
        Assert.Equal(saved, restarted.WorldTime);
        Assert.True(saved > DayCycle.NewWorldTime + DayCycle.HourTicks);
    }

    [Theory]
    [InlineData(null)]          // мир прошлой версии: ключа нет
    [InlineData("'вечер'")]     // испорчен: не число
    [InlineData("-5")]          // испорчен: отрицательное
    public void WorldWithoutValidTime_StartsInMorning(string? stored)
    {
        using (TestServers.Create(savePath: _directory.SaveFile)) { }
        using (var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = stored is null
                ? "DELETE FROM meta WHERE key = 'time'"
                : $"UPDATE meta SET value = {stored} WHERE key = 'time'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using var opened = TestServers.Create(savePath: _directory.SaveFile);
        Assert.Equal(DayCycle.NewWorldTime, opened.WorldTime);
    }
}
