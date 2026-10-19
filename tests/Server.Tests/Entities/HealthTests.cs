using System.Numerics;
using Api;
using Server.Entities;
using Server.Tests.Storage;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;
using static Server.Tests.TestServers;

namespace Server.Tests.Entities;

/// <summary>Здоровье игрока: урон от падения, восстановление, смерть с выпадением вещей, возрождение, сохранение.</summary>
public sealed class HealthTests : IDisposable
{
    private static readonly ContentRegistries Content = GameContent.Load();
    private static readonly ItemId Dirt = Content.Items.Get("base:dirt").Id;
    private static readonly ItemId Pickaxe = Content.Items.Get("base:wooden_pickaxe").Id;

    private readonly TempDirectory _directory = new();
    private readonly List<ServerMessage> _received = [];
    private GameServer _server;
    private TestClient _client;
    private int _teleportId;

    public HealthTests()
    {
        _server = Create(savePath: _directory.SaveFile);
        _client = JoinAndLoad(_server, _received);
    }

    public void Dispose()
    {
        _server.Dispose();
        _directory.Dispose();
    }

    private int Health => _server.PlayerHealthOf(_client.PlayerId);
    private Inventory Inventory => _server.PlayerInventory(_client.PlayerId);
    private Vector3d Feet => _server.PlayerPosition(_client.PlayerId);

    private List<ServerMessage> Receive()
    {
        var messages = new List<ServerMessage>();
        while (_client.TryReceive(out var message))
        {
            messages.Add(message);
            if (message is PlayerTeleport teleport)
                _teleportId = teleport.TeleportId;
        }
        _received.AddRange(messages);
        return messages;
    }

    /// <summary>Поверхность под точкой появления: ноги на верхнем блоке столбца.</summary>
    private Vector3d Ground()
    {
        var spawn = Feet;
        var top = TopBlock(_server.World, (int)Math.Floor(spawn.X), (int)Math.Floor(spawn.Z));
        return new Vector3d(spawn.X, top.Y + 1, spawn.Z);
    }

    /// <summary>
    /// Падение с высоты <paramref name="height"/> над землёй, как у настоящего клиента: тело считается той же физикой
    /// по тикам, каждая позиция уходит серверу и проверяется им.
    /// </summary>
    private void FallFrom(double height)
    {
        var ground = Ground();
        Teleport(_server, _client, ground + new Vector3d(0, height, 0));
        _server.Tick();
        Receive();

        var state = new PlayerState(Feet);
        var registry = Content.Blocks;
        for (var tick = 0; tick < 200 && !(state.OnGround && tick > 0); tick++)
        {
            state = PlayerPhysics.Step(state, new PlayerInput(Vector2.Zero), _server.World.Chunks, registry);
            _client.Send(new PlayerMove(state.Position, default, _teleportId));
            _server.Tick();
            Receive();
        }
        Assert.Equal(ground.Y, Feet.Y, 1e-9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(3.9, 0)]
    [InlineData(4, 1)]
    [InlineData(10, 7)]
    [InlineData(25.5, 22)]
    public void FallDamage_Rule(double distance, int damage) => Assert.Equal(damage, PlayerHealth.FallDamage(distance));

    [Fact]
    public void Join_SendsFullHealth()
    {
        Assert.Contains(new HealthChanged(PlayerHealth.Max, PlayerHealth.Max), _received);
        Assert.Equal(PlayerHealth.Max, Health);
    }

    /// <summary>Падение с 3 блоков не ранит, с 10 — отнимает 7; клиенту приходит новое здоровье.</summary>
    [Fact]
    public void Fall_ThreeHarmless_TenTakesSeven()
    {
        FallFrom(3);
        Assert.Equal(PlayerHealth.Max, Health);

        FallFrom(10);
        Assert.Equal(PlayerHealth.Max - 7, Health);
        Assert.Contains(new HealthChanged(PlayerHealth.Max - 7, PlayerHealth.Max), _received);
    }

    [Fact]
    public void Creative_FallDoesNotHurt()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);

        FallFrom(15);

        Assert.Equal(PlayerHealth.Max, Health);
    }

    /// <summary>В творческом никакой урон не ранит — не только падение.</summary>
    [Fact]
    public void Creative_AnyDamageIgnored()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);

        _server.Damage(_client.PlayerId, 50, "test:hit");
        _server.Tick();

        Assert.Equal(PlayerHealth.Max, Health);
        Assert.DoesNotContain(Receive(), m => m is DeathNotice);
    }

    /// <summary>
    /// Телепорт — не падение: игрок падал с 20 блоков, на середине сервер перенёс его к самой земле. Приземление
    /// считается от точки переноса, а не от высоты, с которой он начал падать.
    /// </summary>
    [Fact]
    public void TeleportMidFall_ResetsFallHeight()
    {
        var ground = Ground();
        Teleport(_server, _client, ground + new Vector3d(0, 20, 0));
        _server.Tick();
        Receive();
        var state = new PlayerState(Feet);
        for (var tick = 0; tick < 10; tick++)
        {
            state = PlayerPhysics.Step(state, new PlayerInput(Vector2.Zero), _server.World.Chunks, Content.Blocks);
            _client.Send(new PlayerMove(state.Position, default, _teleportId));
            _server.Tick();
            Receive();
        }
        Assert.True(Feet.Y > ground.Y + 10, "ещё в воздухе");

        FallFrom(0.5);

        Assert.Equal(PlayerHealth.Max, Health);
    }

    /// <summary>Единица здоровья возвращается через 4 с после урона, не раньше; полное не растёт.</summary>
    [Fact]
    public void Regeneration_OneUnitPerFourSeconds()
    {
        _server.Damage(_client.PlayerId, 5, "test:hit");
        Tick(_server, PlayerHealth.RegenIntervalTicks - 1);
        Assert.Equal(15, Health);
        _server.Tick();
        Assert.Equal(16, Health);

        Tick(_server, PlayerHealth.RegenIntervalTicks * 6);
        Assert.Equal(PlayerHealth.Max, Health);
    }

    /// <summary>
    /// Смерть: весь инвентарь и курсор выпадают на месте (ничего не теряется), клиент получает экран смерти; до
    /// возрождения игрок ничего не делает и не подбирает; возрождение — в точке появления с полным здоровьем.
    /// </summary>
    [Fact]
    public void Death_DropsEverything_BlocksActions_RespawnAtSpawn()
    {
        var spawn = Feet;
        Inventory.Set(0, new ItemStack(Dirt, 40));
        Inventory.Set(20, new ItemStack(Pickaxe, 1));
        Inventory.Set(Inventory.CursorSlot, new ItemStack(Dirt, 3));
        Teleport(_server, _client, Ground() + new Vector3d(5, 0, 0));
        _server.Tick();
        Receive();

        _server.Damage(_client.PlayerId, 25, "test:hit");
        Assert.Contains(new DeathNotice("test:hit"), Receive());
        Assert.Equal(0, Health);
        Assert.All(Enumerable.Range(0, Inventory.SlotCount), slot => Assert.True(Inventory[slot].IsEmpty));
        Assert.Equal(43, OnGround(_server, Dirt));
        Assert.Equal(1, OnGround(_server, Pickaxe));

        // Мёртвый не ходит, не выбрасывает и не подбирает, даже когда задержка подбора прошла.
        var deathPlace = Feet;
        _client.Send(new PlayerMove(deathPlace + new Vector3d(0.3, 0, 0), default, _teleportId));
        Tick(_server, ItemEntities.ThrowPickupDelay + 40);
        Assert.Equal(deathPlace, Feet);
        Assert.Equal(43, OnGround(_server, Dirt));
        Assert.Equal(0, Health);

        _client.Send(new Respawn());
        _server.Tick();
        var afterRespawn = Receive();
        Assert.Equal(spawn, Feet);
        Assert.Equal(PlayerHealth.Max, Health);
        Assert.Contains(afterRespawn, m => m is PlayerTeleport t && t.Position == spawn);
        Assert.Contains(new HealthChanged(PlayerHealth.Max, PlayerHealth.Max), afterRespawn);
        // Вещи остались лежать на месте смерти.
        Assert.Equal(43, OnGround(_server, Dirt));
    }

    [Fact]
    public void Respawn_WhenAlive_IsIgnored()
    {
        Teleport(_server, _client, Ground() + new Vector3d(4, 0, 0));
        _server.Tick();
        var here = Feet;

        _client.Send(new Respawn());
        _server.Tick();

        Assert.Equal(here, Feet);
    }

    [Fact]
    public void Health_SurvivesRestart()
    {
        _server.Damage(_client.PlayerId, 6, "test:hit");
        Restart();

        Assert.Equal(14, Health);
        Assert.Contains(new HealthChanged(14, PlayerHealth.Max), _received);
    }

    /// <summary>Погиб и вышел, не нажав «Возродиться»: при входе — в точке появления с полным здоровьем, вещи на месте смерти.</summary>
    [Fact]
    public void DiedAndQuit_JoinsRespawned()
    {
        var spawn = Feet;
        Inventory.Set(0, new ItemStack(Dirt, 5));
        Teleport(_server, _client, Ground() + new Vector3d(6, 0, 0));
        _server.Tick();
        _server.Damage(_client.PlayerId, 30, "test:hit");
        _server.Tick();

        Restart();

        Assert.Equal(spawn, Feet);
        Assert.Equal(PlayerHealth.Max, Health);
        Assert.Equal(0, Inventory.Count(Dirt));
    }

    private void Restart()
    {
        _server.Dispose();
        _received.Clear();
        _server = Create(savePath: _directory.SaveFile);
        _client = JoinAndLoad(_server, _received);
    }
}
