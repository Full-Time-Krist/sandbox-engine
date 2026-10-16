using System.Numerics;
using Shared;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public sealed class GameServerTests : IDisposable
{
    private readonly List<GameServer> _servers = [];

    public void Dispose() => _servers.ForEach(s => s.Dispose());

    private GameServer Create(int viewDistance = TestServers.ViewDistance, int verticalDistance = TestServers.VerticalDistance)
    {
        var server = TestServers.Create(viewDistance, verticalDistance);
        _servers.Add(server);
        return server;
    }

    private static List<ServerMessage> Drain(IConnection<ClientMessage, ServerMessage> client)
    {
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    /// <summary>Чанки, которые сейчас есть у клиента: ChunkData добавляет, ChunkUnload удаляет. Повторная отправка — ошибка.</summary>
    private static void Apply(HashSet<ChunkPos> clientChunks, IEnumerable<ServerMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message)
            {
                case ChunkData data:
                    Assert.True(clientChunks.Add(data.Position), $"Чанк {data.Position} отправлен повторно");
                    break;
                case ChunkUnload unload:
                    Assert.True(clientChunks.Remove(unload.Position), $"Выгружен чанк {unload.Position}, которого у клиента нет");
                    break;
            }
        }
    }

    private static ChunkPos ChunkOf(Vector3d position) => BlockPos.Containing(position).Chunk;

    private static PlayerChunkView ViewAt(Vector3d position) =>
        new(TestServers.ViewDistance, TestServers.VerticalDistance, ChunkOf(position));

    [Fact]
    public void Join_SendsAcceptedFirst_ThenEveryChunkAroundSpawnOnce()
    {
        var server = Create();
        var messages = new List<ServerMessage>();

        TestServers.JoinAndLoad(server, messages);

        var accepted = Assert.IsType<JoinAccepted>(messages[0]);
        Assert.Equal("base:air", accepted.BlockNames[0]);

        var clientChunks = new HashSet<ChunkPos>();
        Apply(clientChunks, messages);
        var view = ViewAt(accepted.StartPosition);
        Assert.Equal(TestServers.AreaSize(), clientChunks.Count);
        Assert.All(clientChunks, p => Assert.True(view.LoadArea.Contains(view.Center, p), $"{p} вне области загрузки"));
    }

    [Fact]
    public void Join_SendsItemTable_AndWholeInventoryRightAfterAccepted()
    {
        var server = Create();
        var messages = new List<ServerMessage>();

        TestServers.JoinAndLoad(server, messages);

        var accepted = Assert.IsType<JoinAccepted>(messages[0]);
        Assert.Equal("base:air", accepted.ItemNames[0]);
        Assert.Contains("base:stone", accepted.ItemNames);

        // Новый игрок — в режиме мира (по умолчанию выживание), смотрит вдаль, инвентарь пустой.
        Assert.Equal(GameMode.Survival, accepted.Mode);
        Assert.Equal(default, accepted.StartLook);
        var contents = Assert.IsType<InventoryContents>(messages[1]);
        Assert.Equal(Inventory.SlotCount, contents.Slots.Length);
        Assert.Equal(0, contents.SelectedSlot);
        Assert.All(contents.Slots, s => Assert.True(s.IsEmpty));
        Assert.DoesNotContain(messages, m => m is InventoryChanged);
    }

    [Fact]
    public void ChunksPerTick_AreLimited()
    {
        // Первый игрок загружает область; второй входит туда же, когда все чанки уже готовы.
        // Без лимита второй получил бы их все в первом же тике.
        var server = Create(viewDistance: 3, verticalDistance: 1);
        TestServers.JoinAndLoad(server);
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        client.Send(new JoinRequest("Second"));

        var perTick = new List<int>();
        TestServers.TickUntil(server, () =>
        {
            perTick.Add(Drain(client).OfType<ChunkData>().Count());
            return perTick.Sum() >= TestServers.AreaSize(3, 1);
        }, "второй игрок получит все чанки");

        Assert.True(TestServers.AreaSize(3, 1) > GameServer.MaxChunksPerTick * 3);
        Assert.Equal(GameServer.MaxChunksPerTick, perTick.Max());
    }

    [Fact]
    public void MovingFar_UnloadsOldChunks_AndLoadsNewArea()
    {
        var server = Create();
        var messages = new List<ServerMessage>();
        var client = TestServers.JoinAndLoad(server, messages);
        var clientChunks = new HashSet<ChunkPos>();
        Apply(clientChunks, messages);
        var spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;

        var target = spawn + new Vector3(10 * ChunkMath.Size, 0, 0);
        TestServers.Teleport(server, client, target);
        var view = ViewAt(target);
        TestServers.TickUntil(server, () =>
        {
            Apply(clientChunks, Drain(client));
            return view.Missing().All(clientChunks.Contains);
        }, "новая область загружена");

        // У клиента ровно новая область: старые чанки выгружены, ни одного лишнего.
        Assert.Equal(TestServers.AreaSize(), clientChunks.Count);
        Assert.All(clientChunks, p => Assert.True(view.LoadArea.Contains(view.Center, p), $"{p} не выгружен"));
    }

    [Fact]
    public void SmallMovesBackAndForth_DoNotUnloadAnything()
    {
        // Гистерезис: игрок топчется на границе чанка — у клиента ничего не выгружается.
        var server = Create();
        var messages = new List<ServerMessage>();
        var client = TestServers.JoinAndLoad(server, messages);
        var spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;
        var unloads = 0;

        for (var i = 0; i < 6; i++)
        {
            var offset = i % 2 == 0 ? ChunkMath.Size : 0;
            TestServers.Teleport(server, client, spawn + new Vector3(offset, 0, 0));
            for (var t = 0; t < 5; t++)
            {
                server.Tick();
                unloads += Drain(client).OfType<ChunkUnload>().Count();
            }
        }

        Assert.Equal(0, unloads);
    }

    [Fact]
    public void Server_UnloadsChunksNobodyNeeds()
    {
        var server = Create();
        var messages = new List<ServerMessage>();
        var client = TestServers.JoinAndLoad(server, messages);
        var spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;
        var spawnChunk = ChunkOf(spawn);

        var target = spawn + new Vector3(20 * ChunkMath.Size, 0, 0);
        TestServers.Teleport(server, client, target);
        TestServers.TickUntil(server, () =>
        {
            Drain(client);
            return !server.World.Chunks.Contains(spawnChunk);
        }, "сервер выгрузит чанки у точки появления");

        // Всё, что осталось в памяти, нужно игроку.
        var view = ViewAt(target);
        Assert.All(server.World.Chunks.All, c => Assert.True(view.Keeps(c.Position), $"{c.Position} не выгружен"));
    }

    [Fact]
    public void InvalidPosition_IsRejected()
    {
        var server = Create();
        var messages = new List<ServerMessage>();
        var client = TestServers.JoinAndLoad(server, messages);
        var spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;

        client.Send(new PlayerMove(new Vector3(float.NaN, 0, 0), default, TeleportId: 0));
        client.Send(new PlayerMove(new Vector3(0, 0, 3e9f), default, TeleportId: 0));
        for (var t = 0; t < 5; t++)
            server.Tick();

        // Игрок остался на месте: ничего не выгружено и не догружено, клиенту — только возврат на место.
        // Второе сообщение отправлено до первого телепорта (номер 0), его сервер просто игнорирует.
        Assert.Equal([new PlayerTeleport(spawn, 1)], Drain(client));
    }

    [Fact]
    public void BlockChange_IsNotSentToPlayerWithoutThatChunk()
    {
        var server = Create();
        var messages = new List<ServerMessage>();
        var near = TestServers.JoinAndLoad(server, messages);
        var spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;
        var surface = TestServers.TopBlock(server.World, 0, 0);

        // Второй игрок улетает далеко, и чанк с блоком у него выгружается.
        var far = TestServers.JoinAndLoad(server);
        TestServers.Teleport(server, far, spawn + new Vector3(30 * ChunkMath.Size, 0, 0));
        TestServers.TickUntil(server, () => Drain(far).OfType<ChunkUnload>().Any(u => u.Position == surface.Chunk),
            "у второго игрока выгрузится чанк с блоком");
        Drain(far);

        var eye = new Vector3(0.5f, surface.Y + 3.5f, 0.5f);
        TestServers.Teleport(server, near, eye);
        near.Send(new UseItem(Shared.Items.ItemAction.Primary, eye, -Vector3.UnitY));
        server.Tick();

        Assert.Contains(Drain(near), m => m is BlockChanged);
        Assert.DoesNotContain(Drain(far), m => m is BlockChanged);
    }

    [Fact]
    public void MessagesBeforeJoin_AreIgnored()
    {
        var server = Create();
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);

        client.Send(new PlayerMove(default, default, TeleportId: 0));
        server.Tick();

        Assert.Empty(Drain(client));
        Assert.Equal(0, server.World.GeneratingCount);
    }

    [Fact]
    public void SecondJoinRequest_IsIgnored()
    {
        var server = Create();
        var client = TestServers.JoinAndLoad(server);

        client.Send(new JoinRequest("Tester"));
        server.Tick();

        Assert.DoesNotContain(Drain(client), m => m is JoinAccepted);
    }
}
