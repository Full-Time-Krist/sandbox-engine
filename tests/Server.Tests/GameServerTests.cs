using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public class GameServerTests
{
    private static List<ServerMessage> Drain(IConnection<ClientMessage, ServerMessage> client)
    {
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    private static (GameServer Server, IConnection<ClientMessage, ServerMessage> Client) Connect()
    {
        var server = new GameServer();
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        return (server, client);
    }

    [Fact]
    public void Join_SendsAcceptedFirst_ThenWholeWorld()
    {
        var (server, client) = Connect();

        client.Send(new JoinRequest("Tester"));
        server.Tick();
        var messages = Drain(client);

        var accepted = Assert.IsType<JoinAccepted>(messages[0]);
        Assert.Equal("base:air", accepted.BlockNames[0]);

        var chunks = messages.Skip(1).Select(m => Assert.IsType<ChunkData>(m)).ToList();
        var expected = (World.MaxChunk.X - World.MinChunk.X + 1)
                       * (World.MaxChunk.Y - World.MinChunk.Y + 1)
                       * (World.MaxChunk.Z - World.MinChunk.Z + 1);
        Assert.Equal(expected, chunks.Count);
        Assert.Equal(expected, chunks.Select(c => c.Position).Distinct().Count());
        Assert.All(chunks, c => Assert.Equal(c.Position, c.ToChunk().Position));
    }

    [Fact]
    public void Spawn_IsAboveGround_InsideWorld()
    {
        var (server, client) = Connect();
        client.Send(new JoinRequest("Tester"));
        server.Tick();

        var spawn = Assert.IsType<JoinAccepted>(Drain(client)[0]).SpawnPosition;
        var spawnBlock = BlockPos.Containing(spawn);

        Assert.True(server.World.Chunks.Contains(spawnBlock.Chunk), $"Точка появления {spawnBlock} вне мира");
        Assert.True(server.World.Chunks.GetBlock(spawnBlock).IsAir, "Точка появления внутри блока");
    }

    [Fact]
    public void MessagesBeforeJoin_AreIgnored()
    {
        var (server, client) = Connect();

        client.Send(new PlayerMove(default));
        server.Tick();

        Assert.Empty(Drain(client));
    }

    [Fact]
    public void SecondJoinRequest_IsIgnored()
    {
        var (server, client) = Connect();
        client.Send(new JoinRequest("Tester"));
        server.Tick();
        Drain(client);

        client.Send(new JoinRequest("Tester"));
        server.Tick();

        Assert.Empty(Drain(client));
    }
}
