using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public sealed class GameServerTests : IDisposable
{
    private readonly List<GameServer> _servers = [];

    public void Dispose() => _servers.ForEach(s => s.Dispose());

    private static List<ServerMessage> Drain(IConnection<ClientMessage, ServerMessage> client)
    {
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    private (GameServer Server, IConnection<ClientMessage, ServerMessage> Client) Connect(bool waitForWorld = true)
    {
        var server = waitForWorld ? TestServers.CreateLoaded() : new GameServer(TestServers.Seed);
        _servers.Add(server);
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
        Assert.Equal(World.TotalChunks, chunks.Count);
        Assert.Equal(World.TotalChunks, chunks.Select(c => c.Position).Distinct().Count());
        Assert.All(chunks, c => Assert.Equal(c.Position, c.ToChunk().Position));
    }

    [Fact]
    public void JoinDuringGeneration_EventuallyReceivesEveryChunkOnce()
    {
        // Игрок входит сразу, пока мир ещё генерируется: готовые чанки он получает при входе,
        // остальные — по мере готовности. Ни один не должен потеряться или прийти дважды.
        var (server, client) = Connect(waitForWorld: false);
        client.Send(new JoinRequest("Tester"));

        var received = new List<ChunkPos>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (received.Count < World.TotalChunks && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            server.Tick();
            received.AddRange(Drain(client).OfType<ChunkData>().Select(c => c.Position));
            Thread.Sleep(1);
        }

        Assert.Equal(World.TotalChunks, received.Count);
        Assert.Equal(World.TotalChunks, received.Distinct().Count());
        Assert.Equal(0, server.World.PendingChunks);
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
