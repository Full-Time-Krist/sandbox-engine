using System.Numerics;
using Server.Entities;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Mobs;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests.Entities;

/// <summary>Мобы на сервере (шаг 9.1): появление, падение и ходьба той же физикой, что у игрока, рассылка клиенту, яйца призыва.</summary>
public sealed class MobTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;
    private readonly ContentRegistries _content = GameContent.Load();
    private readonly MobDefinition _zombie;
    private readonly BlockPos _surface;

    public MobTests()
    {
        _client = TestServers.JoinAndLoad(_server);
        _zombie = _content.Mobs.Get("base:zombie");
        _surface = TestServers.TopBlock(_server.World, 0, 0);
    }

    public void Dispose() => _server.Dispose();

    private Vector3d OnSurface => new(_surface.X + 0.5, _surface.Y + 1, _surface.Z + 0.5);

    private List<Shared.Entities.EntityId> Mobs() => [.. _server.Entities.Query<Mob>()];

    /// <summary>
    /// Моб без мозга (<see cref="Brain"/>): ИИ его не ведёт, тест двигает его сам через <see cref="Mob.Move"/> — так
    /// проверяется физика ходьбы и рассылка отдельно от поведения (шаг 9.3).
    /// </summary>
    private bool SpawnPuppet(Vector3d feet)
    {
        if (!_server.ModWorld.TrySpawnMob(_zombie.Id, feet))
            return false;
        foreach (var mob in Mobs())
            _server.Entities.Remove<Brain>(mob);
        return true;
    }

    private List<ServerMessage> Drain()
    {
        var messages = new List<ServerMessage>();
        while (_client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    [Fact]
    public void TrySpawnMob_OnFreeGround_CreatesMobOfThatKind_WithFullHealth()
    {
        Assert.True(_server.ModWorld.TrySpawnMob(_zombie.Id, OnSurface));

        var mob = Assert.Single(Mobs());
        Assert.Equal(_zombie.Id, _server.Entities.Get<Mob>(mob).Kind);
        Assert.Equal(OnSurface, _server.Entities.Get<Position>(mob).Value);
        Assert.Equal((20, 20), (_server.Entities.Get<Health>(mob).Current, _server.Entities.Get<Health>(mob).Max));
    }

    /// <summary>Нельзя: внутри земли, в незагруженном чанке, неизвестный вид, нечисловая позиция.</summary>
    [Fact]
    public void TrySpawnMob_Refuses_InsideBlocks_Unloaded_UnknownKind()
    {
        Assert.False(_server.ModWorld.TrySpawnMob(_zombie.Id, OnSurface - new Vector3d(0, 1.5, 0)));
        Assert.False(_server.ModWorld.TrySpawnMob(_zombie.Id, new Vector3d(1_000_000.5, 100, 0.5)));
        Assert.False(_server.ModWorld.TrySpawnMob(new MobId(999), OnSurface));
        Assert.False(_server.ModWorld.TrySpawnMob(MobId.None, OnSurface));
        Assert.False(_server.ModWorld.TrySpawnMob(_zombie.Id, new Vector3d(double.NaN, 0, 0)));
        Assert.Empty(Mobs());
    }

    [Fact]
    public void Mob_FallsToGround_AndStands()
    {
        Assert.True(SpawnPuppet(OnSurface + new Vector3d(0, 4, 0)));
        var mob = Assert.Single(Mobs());

        TestServers.Tick(_server, 40);

        Assert.Equal(OnSurface.Y, _server.Entities.Get<Position>(mob).Value.Y, 1e-9);
        Assert.True(_server.Entities.Get<Walk>(mob).OnGround);
    }

    /// <summary>Ходьба — физикой игрока со скоростью вида; идущий моб поворачивается по ходу (как камера: +X — π/2).</summary>
    [Fact]
    public void Mob_WalksAtItsSpeed_AndFacesWhereItGoes()
    {
        // Площадка в воздухе: рельеф рядом с точкой появления мешал бы идти.
        var floor = _surface + new BlockPos(0, 6, 0);
        var stone = _content.Blocks.Get("base:stone").Id;
        for (var x = -1; x <= 8; x++)
        for (var z = -1; z <= 1; z++)
        {
            Assert.True(_server.World.TrySetBlock(floor + new BlockPos(x, 0, z), stone));
            for (var y = 1; y <= 3; y++)
                Assert.True(_server.World.TrySetBlock(floor + new BlockPos(x, y, z), Shared.Blocks.BlockId.Air));
        }
        var start = new Vector3d(floor.X + 0.5, floor.Y + 1, floor.Z + 0.5);
        Assert.True(SpawnPuppet(start));
        var mob = Assert.Single(Mobs());
        _server.Entities.Get<Mob>(mob).Move = Vector2.UnitX;

        TestServers.Tick(_server, GameTime.TicksPerSecond);

        var walked = _server.Entities.Get<Position>(mob).Value.X - start.X;
        Assert.InRange(walked, _zombie.Speed * 0.85, _zombie.Speed); // за секунду, с разгоном в пару тиков
        Assert.Equal(MathF.PI / 2, _server.Entities.Get<Facing>(mob).Yaw, 4);
    }

    /// <summary>Клиент узнаёт о мобе в отправленном ему чанке — вид, место, поворот — и получает его движение с поворотом.</summary>
    [Fact]
    public void Client_GetsMobSpawn_AndMovesWithYaw()
    {
        Drain();
        Assert.True(SpawnPuppet(OnSurface));
        _server.Tick();

        var spawned = Assert.Single(Drain().OfType<EntitySpawned>());
        var mob = Assert.Single(Mobs());
        Assert.Equal((mob, EntityKind.Mob, _zombie.Id, OnSurface), (spawned.Entity, spawned.Kind, spawned.Mob, spawned.Position));
        Assert.Equal(_server.Entities.Get<Facing>(mob).Yaw, spawned.Yaw);

        _server.Entities.Get<Mob>(mob).Move = -Vector2.UnitY; // на −Z: yaw 0
        _server.Tick();
        var moved = Assert.Single(Assert.Single(Drain().OfType<EntitiesMoved>()).Entities);
        Assert.Equal((mob, 0f), (moved.Entity, moved.Yaw));
        Assert.Equal(_server.Entities.Get<Position>(mob).Value, moved.Position);
    }

    /// <summary>
    /// Поворот без сдвига тоже рассылается: моб упёрся в угол ямы и поворачивается на месте — клиент должен увидеть
    /// новый поворот, хотя положение то же.
    /// </summary>
    [Fact]
    public void Client_GetsYaw_WhenMobTurnsInPlace()
    {
        var stone = _content.Blocks.Get("base:stone").Id;
        // Яма 1 × 1 на поверхности: стены вокруг клетки над верхним блоком.
        for (var x = -1; x <= 1; x++)
        for (var z = -1; z <= 1; z++)
        for (var y = 1; y <= 3; y++)
        {
            if (x != 0 || z != 0)
                Assert.True(_server.World.TrySetBlock(_surface + new BlockPos(x, y, z), stone));
        }
        Assert.True(SpawnPuppet(OnSurface));
        var mob = Assert.Single(Mobs());
        _server.Entities.Get<Mob>(mob).Move = Vector2.Normalize(new Vector2(1, 1));
        TestServers.Tick(_server, 20); // прижался в угол +X +Z
        Drain();

        _server.Entities.Get<Mob>(mob).Move = Vector2.UnitX; // в стену: ни шагу, только поворот
        _server.Tick();

        var moved = Assert.Single(Assert.Single(Drain().OfType<EntitiesMoved>()).Entities);
        Assert.Equal(MathF.PI / 2, moved.Yaw, 4);
        Assert.Equal(_server.Entities.Get<Position>(mob).Value, moved.Position);
    }

    /// <summary>
    /// Голова (шаг 9.2): клиент получает её поворот относительно тела — и при появлении, и когда моб, не сходя с места
    /// и не поворачивая тела, только повернул голову. Через 0 (с 350° на 10°) — по кратчайшей дуге, а не на −340°.
    /// </summary>
    [Fact]
    public void Client_GetsHeadTurn_RelativeToBody()
    {
        Drain();
        Assert.True(SpawnPuppet(OnSurface));
        var mob = Assert.Single(Mobs());
        _server.Entities.Get<Facing>(mob) = new Facing { Yaw = 0.1f, HeadYaw = MathF.Tau - 0.3f };
        _server.Tick();
        Assert.Equal(-0.4f, Assert.Single(Drain().OfType<EntitySpawned>()).HeadTurn, 4);

        _server.Entities.Get<Facing>(mob).HeadYaw = 0.6f;
        _server.Tick();

        var moved = Assert.Single(Assert.Single(Drain().OfType<EntitiesMoved>()).Entities);
        Assert.Equal((0.1f, _server.Entities.Get<Position>(mob).Value), (moved.Yaw, moved.Position));
        Assert.Equal(0.5f, moved.HeadTurn, 4);

        _server.Tick(); // голова больше не двигалась — нечего и слать
        Assert.Empty(Drain().OfType<EntitiesMoved>());
    }

    /// <summary>
    /// Взгляд (шаг 9.3): идущий моб смотрит на точку <see cref="Mob.LookAt"/> головой, но не дальше 75° от тела; стоящий
    /// поворачивается к ней целиком.
    /// </summary>
    [Fact]
    public void LookAt_HeadTurnsUpTo75Degrees_StandingMobTurnsBody()
    {
        Assert.True(SpawnPuppet(OnSurface));
        var mob = Assert.Single(Mobs());
        ref var state = ref _server.Entities.Get<Mob>(mob);
        state.LookAt = OnSurface + new Vector3d(-5, 1, 0); // на −X: yaw −π/2
        state.Move = new Vector2(0, -1);                   // идёт на −Z: yaw 0
        _server.Tick();

        var facing = _server.Entities.Get<Facing>(mob);
        Assert.Equal(0, facing.Yaw, 4);
        Assert.Equal(-MobAi.MaxHeadTurn, facing.HeadTurn, 4); // хотел бы на −90°, но голова — до −75°

        _server.Entities.Get<Mob>(mob).Move = Vector2.Zero;
        _server.Tick();
        facing = _server.Entities.Get<Facing>(mob);
        var to = OnSurface + new Vector3d(-5, 1, 0) - _server.Entities.Get<Position>(mob).Value; // за тик ходьбы моб чуть сдвинулся
        Assert.Equal(MathF.Atan2((float)to.X, -(float)to.Z), facing.Yaw, 4);
        Assert.Equal(0, facing.HeadTurn, 4);
    }

    /// <summary>Пока мобы не сохраняются (шаг 9.5), выгруженный чанк уносит их с собой.</summary>
    [Fact]
    public void UnloadedChunk_RemovesItsMobs()
    {
        Assert.True(_server.ModWorld.TrySpawnMob(_zombie.Id, OnSurface));
        TestServers.Teleport(_server, _client, OnSurface + new Vector3d(20 * ChunkMath.Size, 0, 0));

        TestServers.TickUntil(_server, () =>
        {
            Drain();
            return !_server.World.Chunks.Contains(_surface.Chunk);
        }, "чанк с мобом выгрузится");

        Assert.Empty(Mobs());
    }

    /// <summary>Яйцо призыва: ПКМ по верху блока ставит моба над ним; в выживании яйцо тратится, в творческом — нет.</summary>
    [Theory]
    [InlineData(GameMode.Survival, 4)]
    [InlineData(GameMode.Creative, 5)]
    public void SpawnEgg_PlacesMobOnTopOfBlock(GameMode mode, int eggsLeft)
    {
        _server.SetGameMode(_client.PlayerId, mode);
        var egg = _content.Items.Get("base:zombie_spawn_egg").Id;
        var inventory = _server.PlayerInventory(_client.PlayerId);
        inventory.Set(inventory.SelectedSlot, new ItemStack(egg, 5));
        var feet = OnSurface + new Vector3d(0, 1, 0);
        TestServers.Teleport(_server, _client, feet);

        _client.Send(new UseItem(ItemAction.Secondary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -Vector3.UnitY));
        _server.Tick();

        var mob = Assert.Single(Mobs());
        Assert.Equal(_zombie.Id, _server.Entities.Get<Mob>(mob).Kind);
        Assert.Equal(OnSurface.Y, _server.Entities.Get<Position>(mob).Value.Y, 1e-9);
        Assert.Equal(new ItemStack(egg, eggsLeft), inventory[inventory.SelectedSlot]);
    }
}
