using System.Numerics;
using Server.Entities;
using Shared;
using Shared.Content;
using Shared.Entities;
using Shared.Items;
using Shared.Protocol;
using Shared.Voxels;
using static Server.Tests.TestServers;

namespace Server.Tests.Entities;

/// <summary>Предметы на земле на сервере: выброс по Q, падение и рассылка клиентам по их чанкам.</summary>
public sealed class ItemEntityTests : IDisposable
{
    private static readonly ItemRegistry Items = GameContent.Load().Items;
    private static readonly ItemId Dirt = Items.Get("base:dirt").Id;

    /// <summary>Задержка подбора для предметов в тестах видимости: игроки в выживании иначе подобрали бы их.</summary>
    private const int NoPickup = 1_000_000;

    private readonly GameServer _server = Create();

    public void Dispose() => _server.Dispose();

    private static List<ServerMessage> Drain(TestClient client)
    {
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    private List<EntityId> DroppedItems()
    {
        var result = new List<EntityId>();
        foreach (var entity in _server.Entities.Query<DroppedItem>())
            result.Add(entity);
        return result;
    }

    private Vector3d PositionOf(EntityId entity) => _server.Entities.Get<Position>(entity).Value;

    /// <summary>Чанк в стороне от игрока, которого у клиента нет (дальность в тестах — 1 чанк).</summary>
    private static Vector3d FarAway => new(1000.5, 40, 1000.5);

    [Fact]
    public void Drop_OneItem_LeavesHand_AndAppearsForClient()
    {
        var client = JoinAndLoad(_server);
        _server.PlayerInventory(client.PlayerId).Set(0, new ItemStack(Dirt, 5));
        Drain(client);

        client.Send(new DropItem(WholeStack: false));
        _server.Tick();

        Assert.Equal(new ItemStack(Dirt, 4), _server.PlayerInventory(client.PlayerId)[0]);
        var entity = Assert.Single(DroppedItems());
        Assert.Equal(new ItemStack(Dirt, 1), _server.Entities.Get<DroppedItem>(entity).Stack);
        var spawned = Assert.Single(Drain(client).OfType<EntitySpawned>());
        Assert.Equal(entity, spawned.Entity);
        Assert.Equal(EntityKind.Item, spawned.Kind);
        Assert.Equal(new ItemStack(Dirt, 1), spawned.Item);
        Assert.Equal(PositionOf(entity), spawned.Position);
    }

    [Fact]
    public void Drop_WholeStack_EmptiesSlot()
    {
        var client = JoinAndLoad(_server);
        _server.PlayerInventory(client.PlayerId).Set(0, new ItemStack(Dirt, 5));

        client.Send(new DropItem(WholeStack: true));
        _server.Tick();

        Assert.True(_server.PlayerInventory(client.PlayerId)[0].IsEmpty);
        Assert.Equal(new ItemStack(Dirt, 5), _server.Entities.Get<DroppedItem>(Assert.Single(DroppedItems())).Stack);
    }

    [Fact]
    public void Drop_EmptyHand_DoesNothing()
    {
        var client = JoinAndLoad(_server);
        Drain(client);

        client.Send(new DropItem(WholeStack: true));
        _server.Tick();

        Assert.Empty(DroppedItems());
        Assert.Empty(Drain(client).OfType<EntitySpawned>());
    }

    /// <summary>
    /// Брошенный предмет летит вперёд по взгляду (игрок смотрит вдоль −Z), падает и ложится на блок; пока летит,
    /// клиент получает его перемещения, а лежащий больше не шлётся.
    /// </summary>
    [Fact]
    public void Dropped_FliesForward_LandsOnBlock_ThenStopsSending()
    {
        var client = JoinAndLoad(_server);
        _server.PlayerInventory(client.PlayerId).Set(0, new ItemStack(Dirt, 1));
        var feet = _server.PlayerPosition(client.PlayerId);
        client.Send(new DropItem(WholeStack: false));
        _server.Tick();
        var entity = Assert.Single(DroppedItems());
        Drain(client);

        TickUntil(_server, () => _server.Entities.Get<Body>(entity).OnGround, "предмет ляжет на землю");
        var moves = Drain(client).OfType<EntitiesMoved>().SelectMany(m => m.Entities).Where(m => m.Entity == entity).ToList();
        var landed = PositionOf(entity);

        Assert.NotEmpty(moves);
        Assert.Equal(landed, moves[^1].Position);
        Assert.True(landed.Z < feet.Z - 0.5, $"предмет улетел вперёд: {landed} от {feet}");
        // Лежит ровно на верхней грани твёрдого блока.
        Assert.Equal(Math.Round(landed.Y), landed.Y, 1e-9);
        Assert.False(_server.World.Chunks.GetBlock(BlockPos.Containing(landed + new Vector3d(0, -0.5, 0))).IsAir);

        Tick(_server, 20);
        Assert.Empty(Drain(client).OfType<EntitiesMoved>());
        Assert.Equal(landed, PositionOf(entity));
    }

    [Fact]
    public void DestroyedEntity_RemovedAtClient()
    {
        var client = JoinAndLoad(_server);
        _server.PlayerInventory(client.PlayerId).Set(0, new ItemStack(Dirt, 1));
        client.Send(new DropItem(WholeStack: false));
        _server.Tick();
        var entity = Assert.Single(DroppedItems());
        Drain(client);

        _server.Entities.Destroy(entity);
        _server.Tick();

        Assert.Equal(entity, Assert.Single(Drain(client).OfType<EntityRemoved>()).Entity);
        _server.Tick();
        Assert.Empty(Drain(client).OfType<EntityRemoved>());
    }

    /// <summary>
    /// Сущность в чанке, которого у клиента нет, ему не шлётся. Игрок пришёл туда — сущность появляется после
    /// своего чанка; ушёл — пропадает вместе с чанком.
    /// </summary>
    [Fact]
    public void Visibility_FollowsChunksSentToClient()
    {
        var client = JoinAndLoad(_server);
        var far = ItemEntities.Spawn(_server.Entities, FarAway, Vector3.Zero, new ItemStack(Dirt, 3), NoPickup);
        var home = PlayerFeet(client);
        var near = ItemEntities.Spawn(_server.Entities, home + new Vector3d(0, 1, 0), Vector3.Zero, new ItemStack(Dirt, 1), NoPickup);
        Tick(_server, 2);
        var first = Drain(client);
        Assert.Equal([near], first.OfType<EntitySpawned>().Select(s => s.Entity));

        // Незагруженный чанк держит предмет на месте, пока туда не придёт игрок.
        Assert.Equal(FarAway, PositionOf(far));

        Teleport(_server, client, FarAway + new Vector3d(0, 2, 0));
        var farChunk = BlockPos.Containing(FarAway).Chunk;
        // Сообщения по тикам: сущность должна прийти в том же тике, что и её чанк, сразу после него.
        var ticks = new List<List<ServerMessage>>();
        TickUntil(_server, () =>
        {
            ticks.Add(Drain(client));
            return ticks[^1].OfType<EntitySpawned>().Any(s => s.Entity == far);
        }, "клиент получит дальний предмет");
        var received = ticks.SelectMany(t => t).ToList();

        var spawnTick = ticks[^1];
        var chunkIndex = spawnTick.FindIndex(m => m is ChunkData data && data.Position == farChunk);
        var spawnIndex = spawnTick.FindIndex(m => m is EntitySpawned s && s.Entity == far);
        Assert.InRange(chunkIndex, 0, spawnIndex - 1);
        Assert.Contains(received.OfType<EntityRemoved>(), r => r.Entity == near);
        Assert.Equal(new ItemStack(Dirt, 3), received.OfType<EntitySpawned>().Single(s => s.Entity == far).Item);
    }

    /// <summary>Два игрока далеко друг от друга: каждый знает только свои сущности.</summary>
    [Fact]
    public void TwoPlayers_EachSeesOnlyEntitiesInTheirChunks()
    {
        var first = JoinAndLoad(_server);
        var second = JoinAndLoad(_server);
        Teleport(_server, second, new Vector3d(FarAway.X, 200, FarAway.Z));
        var farChunk = BlockPos.Containing(new Vector3d(FarAway.X, 200, FarAway.Z)).Chunk;
        TickUntil(_server, () => Drain(second).OfType<ChunkData>().Any(c => c.Position == farChunk), "второй игрок получит свой чанк");
        Drain(first);

        var nearFirst = ItemEntities.Spawn(_server.Entities, PlayerFeet(first) + new Vector3d(0, 1, 0), Vector3.Zero, new ItemStack(Dirt, 1), NoPickup);
        _server.Tick();

        Assert.Equal([nearFirst], Drain(first).OfType<EntitySpawned>().Select(s => s.Entity));
        Assert.Empty(Drain(second).OfType<EntitySpawned>());

        _server.Entities.Destroy(nearFirst);
        _server.Tick();
        Assert.Single(Drain(first).OfType<EntityRemoved>());
        Assert.Empty(Drain(second).OfType<EntityRemoved>());
    }

    private Vector3d PlayerFeet(TestClient client) => _server.PlayerPosition(client.PlayerId);
}
