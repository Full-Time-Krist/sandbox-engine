using Api;
using Server.Tests.Storage;
using Shared;
using Shared.Content;
using Shared.Entities;
using Shared.Items;
using Shared.Networking;
using Shared.Physics;
using Shared.Protocol;

namespace Server.Tests;

public sealed class HealthTests
{
    private sealed class Observer : IMod
    {
        public IPlayer Player = null!;
        public List<PlayerDamaged> Damaged { get; } = [];
        public List<PlayerDied> Died { get; } = [];
        public List<ItemPickedUp> PickedUp { get; } = [];
        public Action<IPlayer>? Joined { get; init; }
        public void Load(IModContext context)
        {
            context.Events.OnPlayerJoined(e => { Player = e.Player; Joined?.Invoke(e.Player); });
            context.Events.OnPlayerDamaged(Damaged.Add);
            context.Events.OnPlayerDied(Died.Add);
            context.Events.OnItemPickedUp(PickedUp.Add);
        }
    }

    [Fact]
    public void Damage_RegeneratesOnePointAfterFourSeconds_AndCreativeIsImmune()
    {
        var observer = new Observer();
        using var server = TestServers.Create(mods: [("observer", observer)]);
        var client = TestServers.JoinAndLoad(server);
        observer.Player.Damage(4, "test");
        Assert.Equal(16, observer.Player.Health);
        TestServers.Tick(server, 79);
        Assert.Equal(16, observer.Player.Health);
        server.Tick();
        Assert.Equal(17, observer.Player.Health);
        var damage = Assert.Single(observer.Damaged);
        Assert.Equal(4, damage.Amount);
        Assert.Equal("test", damage.Reason);
        server.SetGameMode(client.PlayerId, GameMode.Creative);
        observer.Player.Damage(99, "test");
        Assert.Equal(17, observer.Player.Health);
        Assert.Empty(observer.Died);
    }

    [Fact]
    public void Death_DropsEverySlotAndCursorOnce_ThenRespawnsOnlyOnRequest()
    {
        var observer = new Observer();
        using var server = TestServers.Create(mods: [("observer", observer)]);
        var client = TestServers.JoinAndLoad(server);
        var inventory = observer.Player.Inventory;
        var log = GameContent.Load().Items.Get("base:log").Id;
        inventory.Set(0, new ItemStack(log, 12));
        inventory.Set(Inventory.CursorSlot, new ItemStack(log, 3));
        var deathPosition = observer.Player.Position;
        observer.Player.Damage(99, "test");
        observer.Player.Damage(99, "again");
        Assert.Equal(0, observer.Player.Health);
        Assert.All(inventory.CopySlots(), stack => Assert.True(stack.IsEmpty));
        Assert.Equal(15, server.GroundItems.Sum(item => item.Stack.Count));
        server.Tick();
        Assert.Single(observer.Died);
        Assert.Single(observer.Damaged);
        var messages = Drain(client);
        Assert.Contains(messages, m => m is HealthChanged { Health: 0, IsDead: true });
        Assert.Equal(deathPosition, observer.Player.Position);
        client.Send(new PlayerMove(deathPosition + new Vector3d(0, 1, 0), default, 0));
        client.Send(new SetGameMode(GameMode.Creative));
        server.Tick();
        Assert.Equal(deathPosition, observer.Player.Position);
        Assert.Equal(GameMode.Survival, observer.Player.Mode);
        TestServers.Tick(server, 80);
        Assert.Empty(observer.PickedUp);
        Assert.All(inventory.CopySlots(), stack => Assert.True(stack.IsEmpty));
        Assert.Equal(15, server.GroundItems.Sum(item => item.Stack.Count));
        client.Send(new RespawnRequest());
        server.Tick();
        Assert.Equal(20, observer.Player.Health);
        Assert.Equal(server.World.SpawnPosition, observer.Player.Position);
        Assert.Contains(Drain(client), m => m is PlayerTeleport);
        client.Send(new RespawnRequest());
        server.Tick();
        Assert.DoesNotContain(Drain(client), m => m is PlayerTeleport);
    }

    [Fact]
    public void DeathDuringJoin_BeforeChunksLoad_PreservesLootOnImmediateExit()
    {
        using var temp = new TempDirectory();
        var log = GameContent.Load().Items.Get("base:log").Id;
        var observer = new Observer
        {
            Joined = player =>
            {
                player.Inventory.Set(0, new ItemStack(log, 9));
                player.Damage(20, "join");
            }
        };
        using (var server = TestServers.Create(savePath: temp.SaveFile, mods: [("observer", observer)]))
        {
            var (client, connection) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
            server.Connect(connection);
            client.Send(new JoinRequest("Tester"));
            server.Tick();
            Assert.Equal(0, observer.Player.Health);
            Assert.Equal(9, server.GroundItems.Sum(item => item.Stack.Count));
        }
        using (var server = TestServers.Create(savePath: temp.SaveFile))
        {
            TestServers.JoinAndLoad(server);
            Assert.Equal(9, server.GroundItems.Sum(item => item.Stack.Count));
        }
    }

    [Theory]
    [InlineData(3, 20)]
    [InlineData(10, 13)]
    public void AcceptedFall_DamagesByHeightBeyondThreeBlocks(int height, int health)
    {
        var observer = new Observer();
        using var server = TestServers.Create(mods: [("observer", observer)]);
        var client = TestServers.JoinAndLoad(server);
        var floor = server.World.SpawnPosition;
        // Чистая колонна над точкой появления, одна и та же физика у честного клиента и сервера.
        var start = floor + new Vector3d(0, height, 0);
        TestServers.Teleport(server, client, start);
        int teleportId = Drain(client).OfType<PlayerTeleport>().Last().TeleportId;
        var state = new PlayerState(start);
        var blocks = GameContent.Load().Blocks;
        for (int i = 0; i < 100 && !state.OnGround; i++)
        {
            state = PlayerPhysics.Step(state, default, server.World.Chunks, blocks);
            client.Send(new PlayerMove(state.Position, default, teleportId));
            server.Tick();
            Assert.DoesNotContain(Drain(client), m => m is PlayerTeleport);
        }
        Assert.True(state.OnGround);
        Assert.Equal(health, observer.Player.Health);
    }

    [Fact]
    public void HealthAndDeath_PersistAcrossRestart()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "health.db");
        var observer = new Observer();
        using (var server = TestServers.Create(savePath: path, mods: [("observer", observer)]))
        {
            TestServers.JoinAndLoad(server);
            observer.Player.Damage(6, "test");
        }
        var next = new Observer();
        using (var server = TestServers.Create(savePath: path, mods: [("observer", next)]))
        {
            TestServers.JoinAndLoad(server);
            Assert.Equal(14, next.Player.Health);
            next.Player.Damage(99, "test");
        }
        var dead = new Observer();
        using (var server = TestServers.Create(savePath: path, mods: [("observer", dead)]))
        {
            var messages = new List<ServerMessage>();
            TestServers.JoinAndLoad(server, messages);
            Assert.Equal(0, dead.Player.Health);
            Assert.Contains(messages, m => m is HealthChanged { IsDead: true });
        }
    }

    private static List<ServerMessage> Drain(TestServers.TestClient client)
    {
        var result = new List<ServerMessage>();
        while (client.TryReceive(out var message)) result.Add(message);
        return result;
    }
}
