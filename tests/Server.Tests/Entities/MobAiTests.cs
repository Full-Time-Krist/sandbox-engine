using Server.Entities;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Entities;
using Shared.Mobs;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Tests.Entities;

/// <summary>
/// Поведение мобов по шагам (шаг 9.3) на настоящем сервере: площадка в воздухе (рельеф не мешает), игрок на одном её краю.
/// Площадка — пол из камня на высоте <see cref="_floor"/>, X от −12 до 12, Z от −6 до 6, над полом 4 блока воздуха.
/// </summary>
public sealed class MobAiTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;
    private readonly ContentRegistries _content = GameContent.Load();
    private readonly BlockId _stone;
    private readonly int _floor;

    public MobAiTests()
    {
        _client = TestServers.JoinAndLoad(_server);
        _stone = _content.Blocks.Get("base:stone").Id;
        _floor = TestServers.TopBlock(_server.World, 0, 0).Y + 6;
        for (var x = -12; x <= 12; x++)
        for (var z = -6; z <= 6; z++)
        {
            Assert.True(_server.World.TrySetBlock(new BlockPos(x, _floor, z), _stone));
            for (var y = 1; y <= 4; y++)
                Assert.True(_server.World.TrySetBlock(new BlockPos(x, _floor + y, z), BlockId.Air));
        }
        TestServers.Teleport(_server, _client, At(-6, 0));
    }

    public void Dispose() => _server.Dispose();

    private Vector3d At(double x, double z) => new(x + 0.5, _floor + 1, z + 0.5);

    private EntityId Spawn(string mob, Vector3d feet)
    {
        Assert.True(_server.ModWorld.TrySpawnMob(_content.Mobs.Get(mob).Id, feet));
        List<EntityId> mobs = [.. _server.Entities.Query<Mob>()];
        return mobs[^1];
    }

    private ref Brain BrainOf(EntityId mob) => ref _server.Entities.Get<Brain>(mob);

    private Vector3d PositionOf(EntityId mob) => _server.Entities.Get<Position>(mob).Value;

    private double HorizontalToPlayer(EntityId mob)
    {
        var delta = PositionOf(mob) - _server.PlayerPosition(_client.PlayerId);
        return Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
    }

    /// <summary>Стена поперёк площадки по X = <paramref name="x"/> от z1 до z2, высотой 3.</summary>
    private void Wall(int x, int z1, int z2)
    {
        for (var z = z1; z <= z2; z++)
        for (var y = 1; y <= 3; y++)
            Assert.True(_server.World.TrySetBlock(new BlockPos(x, _floor + y, z), _stone));
    }

    /// <summary>
    /// Зомби видит игрока в выживании и идёт к нему; когда между ними встаёт стена — обходит её через проём у края
    /// (ни разу не оказавшись в стене), подходит вплотную, останавливается и смотрит на игрока.
    /// </summary>
    [Fact]
    public void Zombie_SpotsPlayer_ChasesAroundWall_StopsNearAndLooks()
    {
        var zombie = Spawn("base:zombie", At(6, 0));
        TestServers.TickUntil(_server, () => BrainOf(zombie).State == BrainState.Chase, "зомби заметил игрока", 5);

        Wall(0, -6, 4); // проём — z 5 и 6
        var crossedThroughGap = false;
        TestServers.TickUntil(_server, () =>
        {
            var p = PositionOf(zombie);
            if (Math.Abs(p.X) < 0.8)
            {
                Assert.True(p.Z > 4.6, $"зомби в стене: {p}");
                crossedThroughGap = true;
            }
            return HorizontalToPlayer(zombie) < HostileMeleeBehavior.StopDistance + 0.3;
        }, "зомби подошёл к игроку", 30);
        Assert.True(crossedThroughGap);

        TestServers.Tick(_server, 10);
        Assert.InRange(HorizontalToPlayer(zombie), 0.5, HostileMeleeBehavior.StopDistance + 0.3); // стоит рядом, не лезет в игрока
        Assert.Equal(BrainState.Chase, BrainOf(zombie).State);
        // Смотрит на игрока: поворот головы — на него (как у камеры: на −X — yaw −π/2).
        AssertLooksAtPlayer(zombie);

        // Игрок отошёл вбок, но остался рядом: зомби не идёт, а поворачивается к нему.
        var here = PositionOf(zombie);
        TestServers.Teleport(_server, _client, new Vector3d(here.X, here.Y, here.Z + 1));
        TestServers.Tick(_server, 2);
        AssertLooksAtPlayer(zombie);
    }

    private void AssertLooksAtPlayer(EntityId zombie)
    {
        var facing = _server.Entities.Get<Facing>(zombie);
        var to = _server.PlayerPosition(_client.PlayerId) - PositionOf(zombie);
        Assert.Equal(0, MathF.IEEERemainder(facing.HeadYaw - MathF.Atan2((float)to.X, -(float)to.Z), MathF.Tau), 2);
    }

    /// <summary>Игрока в творческом зомби не видит; в выживании — замечает; ушёл в творческий — бросает погоню.</summary>
    [Fact]
    public void Zombie_IgnoresCreative_ChasesSurvival_DropsOnModeChange()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        var zombie = Spawn("base:zombie", At(4, 0));
        for (var tick = 0; tick < 3 * GameTime.TicksPerSecond; tick++)
        {
            _server.Tick();
            Assert.NotEqual(BrainState.Chase, BrainOf(zombie).State); // даже на тик не «замечает»
        }

        _server.SetGameMode(_client.PlayerId, GameMode.Survival);
        TestServers.TickUntil(_server, () => BrainOf(zombie).State == BrainState.Chase, "погоня в выживании", 5);

        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        _server.Tick();
        Assert.Equal(BrainState.Idle, BrainOf(zombie).State);
        Assert.Null(_server.Entities.Get<Mob>(zombie).LookAt);
    }

    /// <summary>Сквозь стену не видит: игрок рядом, но за глухой стеной — погони нет.</summary>
    [Fact]
    public void Zombie_DoesNotSeeThroughWall()
    {
        Wall(0, -6, 6);
        var zombie = Spawn("base:zombie", At(4, 0));
        for (var tick = 0; tick < 3 * GameTime.TicksPerSecond; tick++)
        {
            _server.Tick();
            Assert.NotEqual(BrainState.Chase, BrainOf(zombie).State);
        }
    }

    /// <summary>Игрок ушёл дальше <see cref="HostileMeleeBehavior.LoseRange"/> — погоня брошена, зомби снова бродит.</summary>
    [Fact]
    public void Zombie_LosesPlayer_WhoWentFar()
    {
        var zombie = Spawn("base:zombie", At(4, 0));
        TestServers.TickUntil(_server, () => BrainOf(zombie).State == BrainState.Chase, "погоня", 5);

        TestServers.Teleport(_server, _client, At(-12, 0) + new Vector3d(-HostileMeleeBehavior.LoseRange, 0, 0));
        _server.Tick();

        Assert.Equal(BrainState.Idle, BrainOf(zombie).State);
    }

    /// <summary>Овца бродит: стоит, идёт к случайной точке рядом, снова стоит; к игроку не идёт.</summary>
    [Fact]
    public void Sheep_Wanders_IdleAndWalk_NeverChases()
    {
        var sheep = Spawn("base:sheep", At(4, 0));
        var start = PositionOf(sheep);
        var states = new HashSet<BrainState>();
        var farthest = 0.0;
        for (var tick = 0; tick < 20 * GameTime.TicksPerSecond; tick++)
        {
            _server.Tick();
            states.Add(BrainOf(sheep).State);
            farthest = Math.Max(farthest, Vector3d.Distance(PositionOf(sheep), start));
        }

        Assert.Equal([BrainState.Idle, BrainState.Wander], states.Order());
        Assert.InRange(farthest, 1, MobAi.WanderRadius * 2 + 1);
    }

    /// <summary>Ударенная овца (шаг 9.4 вызовет <see cref="MobAi.Panic"/>) бежит прочь от удара бегом, потом успокаивается.</summary>
    [Fact]
    public void Sheep_Panics_RunsAwayFast_ThenCalmsDown()
    {
        var sheep = Spawn("base:sheep", At(2, 0));
        _server.Tick();
        var start = PositionOf(sheep);
        MobAi.Panic(_server.Entities, sheep, start - new Vector3d(1, 0, 0), new Random(1));

        TestServers.Tick(_server, GameTime.TicksPerSecond);
        Assert.True(_server.Entities.Get<Mob>(sheep).Sprint);
        Assert.Equal(BrainState.Flee, BrainOf(sheep).State);
        var run = PositionOf(sheep) - start;
        Assert.True(run.X > 1.5, $"за секунду убежала лишь на {run}");
        // Быстрее шага: шагом за секунду не дальше скорости вида.
        Assert.True(Math.Sqrt(run.X * run.X + run.Z * run.Z) > _content.Mobs.Get("base:sheep").Speed * 1.2, $"бег {run}");

        TestServers.Tick(_server, 5 * GameTime.TicksPerSecond);
        Assert.NotEqual(BrainState.Flee, BrainOf(sheep).State);
        Assert.False(_server.Entities.Get<Mob>(sheep).Sprint);
    }
}

/// <summary>Бюджет поиска пути и проверка поведений по имени — на собранном вручную мире без сервера.</summary>
public class MobAiBudgetTests
{
    private readonly BlockRegistry _blocks = new();
    private readonly ChunkMap _chunks = new();
    private readonly MobRegistry _mobs = new();
    private readonly EntityWorld _entities = new();

    public MobAiBudgetTests()
    {
        var stone = _blocks.Register("test:stone", isOpaque: true).Id;
        _blocks.Freeze();
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            _chunks.Set(new Chunk(new ChunkPos(x, y, z)));
        for (var x = -32; x <= 31; x++)
        for (var z = -32; z <= 31; z++)
            _chunks.TrySetBlock(new BlockPos(x, -1, z), stone);
        // Замурованная клетка (20, 0, 20): пути к ней нет, и каждый поиск тратит весь предел клеток.
        for (var x = 19; x <= 21; x++)
        for (var z = 19; z <= 21; z++)
        for (var y = 0; y < 3; y++)
        {
            if (x != 20 || z != 20)
                _chunks.TrySetBlock(new BlockPos(x, y, z), stone);
        }
        _mobs.Register("test:zombie", 20, 2.3f, 0.6, 1.95, MobAi.HostileMeleeName);
        _mobs.Freeze();
    }

    /// <summary>
    /// Тридцать мобов просят путь в одном тике: поисков за тик — не больше, чем помещается в бюджет (по
    /// <see cref="PathFinder.MaxNodes"/> на поиск), остальные ждут следующих тиков — и в итоге путь получают все.
    /// </summary>
    [Fact]
    public void ManyMobs_SearchWithinTickBudget_AllEventuallyServed()
    {
        var ai = new MobAi(_mobs, new Random(1));
        var definition = _mobs.Get("test:zombie");
        var mobs = new List<EntityId>();
        for (var i = 0; i < 30; i++)
        {
            var mob = MobEntities.Spawn(_entities, definition, new Vector3d(-20.5 + i, 0, -20.5), 0);
            ref var brain = ref _entities.Get<Brain>(mob);
            brain.State = BrainState.Wander;
            MobAi.RequestPath(ref brain, new Vector3d(20.5, 0, 20.5));
            mobs.Add(mob);
        }

        var ticks = 0;
        while (mobs.Any(m => _entities.Get<Brain>(m).WantsPath))
        {
            ai.Run(_entities, _chunks, _blocks, []);
            ticks++;
            Assert.InRange(ai.SearchesLastTick, 1, MobAi.NodesPerTick / PathFinder.MaxNodes);
            Assert.True(ticks < 30, "поиски не закончились");
        }
        Assert.Equal(30 / (MobAi.NodesPerTick / PathFinder.MaxNodes) + (30 % (MobAi.NodesPerTick / PathFinder.MaxNodes) > 0 ? 1 : 0), ticks);
        Assert.All(mobs, m => Assert.False(_entities.Get<Brain>(m).Path?.Complete ?? false));
    }

    /// <summary>Моб с поведением, которого нет, — ошибка контента при запуске, а не молчащий моб.</summary>
    [Fact]
    public void UnknownBehavior_IsContentError()
    {
        var mobs = new MobRegistry();
        mobs.Register("test:ghost", 5, 1, 0.5, 0.5, "test:haunt");
        mobs.Freeze();

        var error = Assert.Throws<ContentLoadException>(() => new MobAi(mobs, new Random(1)));
        Assert.Contains("test:ghost", error.Message);
        Assert.Contains("test:haunt", error.Message);
    }
}
