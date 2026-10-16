using Shared;
using Shared.Protocol;

namespace Server.Tests;

/// <summary>Смена режима по запросу клиента (клавиша G).</summary>
public sealed class GameModeTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;

    public GameModeTests() => _client = TestServers.JoinAndLoad(_server);

    public void Dispose() => _server.Dispose();

    private List<ServerMessage> Send(params ClientMessage[] messages)
    {
        foreach (var message in messages)
            _client.Send(message);
        _server.Tick();

        var received = new List<ServerMessage>();
        while (_client.TryReceive(out var message))
        {
            if (message is not ChunkData and not ChunkUnload)
                received.Add(message);
        }
        return received;
    }

    [Fact]
    public void ToCreative_ModeChangedSent_NoTeleport()
    {
        Assert.Equal([new GameModeChanged(GameMode.Creative)], Send(new SetGameMode(GameMode.Creative)));
    }

    [Fact]
    public void ToSurvival_ModeChangedThenTeleportInPlace()
    {
        Send(new SetGameMode(GameMode.Creative));
        var position = _server.PlayerPosition(_client.PlayerId);

        var messages = Send(new SetGameMode(GameMode.Survival));

        // Телепорт на месте: перемещения, посчитанные клиентом ещё в полёте, станут устаревшими.
        Assert.Equal([new GameModeChanged(GameMode.Survival), new PlayerTeleport(position, 1)], messages);
    }

    [Fact]
    public void MovesSentBeforeSwitchToSurvival_AreIgnored()
    {
        Send(new SetGameMode(GameMode.Creative));
        var start = _server.PlayerPosition(_client.PlayerId);
        // Посчитано клиентом ещё в полёте. В запас пути выживания укладывается, так что без телепорта
        // при смене режима сервер бы его принял, а клиент, узнав о смене, продолжил бы с полётной скоростью.
        var flight = start + new Vector3d(0, 1, 0);

        // Клиент ещё не знает о смене режима и шлёт позицию со старым номером телепорта.
        var messages = Send(new SetGameMode(GameMode.Survival), new PlayerMove(flight, default, TeleportId: 0));

        Assert.Equal(start, _server.PlayerPosition(_client.PlayerId));
        Assert.Single(messages.OfType<PlayerTeleport>());
    }

    [Fact]
    public void SameMode_OrUnknownMode_ChangesNothing()
    {
        Assert.Empty(Send(new SetGameMode(GameMode.Survival), new SetGameMode((GameMode)7)));
    }
}
