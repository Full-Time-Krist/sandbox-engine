using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Protocol;

namespace Client.Tests;

public class ClientEntitiesTests
{
    private static readonly EntityId A = new(0, 1);
    private static readonly EntityId B = new(1, 1);
    private static readonly ItemStack Stack = new(new ItemId(3), 2);

    private readonly ClientEntities _entities = new();

    private static EntitySpawned Spawn(EntityId id, double x) => new(id, EntityKind.Item, new Vector3d(x, 0, 0), Stack);

    private static EntitiesMoved Moved(params (EntityId Id, double X)[] moves) =>
        new([.. moves.Select(m => new EntityPosition(m.Id, new Vector3d(m.X, 0, 0)))]);

    private ClientEntity Get(EntityId id) => _entities.TryGet(id, out var entity) ? entity : throw new KeyNotFoundException(id.ToString());

    /// <summary>Моб (шаг 9.1): помнит вид и поворот; поворот между тиками — по кратчайшей дуге (через 0, а не через π).</summary>
    [Fact]
    public void Mob_KeepsKind_AndYawTurnsTheShortWay()
    {
        var kind = new Shared.Mobs.MobId(2);
        _entities.Apply(new EntitySpawned(A, EntityKind.Mob, new Vector3d(0, 0, 0), default, kind, Yaw: MathF.Tau - 0.2f));
        Assert.Equal((EntityKind.Mob, kind), (Get(A).Kind, Get(A).Mob));

        _entities.BeginTick();
        _entities.Apply(new EntitiesMoved([new EntityPosition(A, new Vector3d(1, 0, 0), 0.2f)]));

        Assert.Equal(MathF.Tau - 0.2f, Get(A).YawAt(0), 4);
        Assert.Equal(MathF.Tau, Get(A).YawAt(0.5f), 4); // половина пути — через 0 (2π), а не через π
        Assert.Equal(MathF.Tau + 0.2f, Get(A).YawAt(1), 4);
    }

    [Fact]
    public void Spawn_Move_Remove()
    {
        Assert.True(_entities.Apply(Spawn(A, 1)));
        _entities.Apply(Spawn(B, 5));
        _entities.Apply(Moved((A, 2)));

        Assert.Equal(new Vector3d(2, 0, 0), Get(A).Current);
        Assert.Equal(new Vector3d(5, 0, 0), Get(B).Current);
        Assert.Equal(Stack, Get(A).Item);

        _entities.Apply(new EntityRemoved(A));
        Assert.False(_entities.TryGet(A, out _));
        Assert.Equal(1, _entities.Count);
    }

    /// <summary>Положение плавно идёт от прошлого тика к последнему; новая сущность не «прилетает» из начала координат.</summary>
    [Fact]
    public void Interpolation_BetweenLastTwoTicks()
    {
        _entities.Apply(Spawn(A, 10));
        Assert.Equal(new Vector3d(10, 0, 0), Get(A).At(0));

        _entities.BeginTick();
        _entities.Apply(Moved((A, 12)));

        Assert.Equal(new Vector3d(10, 0, 0), Get(A).At(0));
        Assert.Equal(new Vector3d(11, 0, 0), Get(A).At(0.5f));
        Assert.Equal(new Vector3d(12, 0, 0), Get(A).At(1));

        // Тик без перемещения: сущность стоит в последней точке, а не откатывается назад.
        _entities.BeginTick();
        Assert.Equal(new Vector3d(12, 0, 0), Get(A).At(0));
        Assert.Equal(new Vector3d(12, 0, 0), Get(A).At(1));
    }

    /// <summary>
    /// Шаг моба (шаг 9.2): путь по горизонтали копится (вертикаль — падение — не шаг), размах тянется к скорости за
    /// несколько тиков и так же гаснет, когда моб встал; между тиками оба — по доле тика.
    /// </summary>
    [Fact]
    public void MobWalk_AccumulatesHorizontalPath_SwingFollowsSpeed()
    {
        _entities.Apply(new EntitySpawned(A, EntityKind.Mob, new Vector3d(0, 5, 0), default, new Shared.Mobs.MobId(1)));
        const double PerTick = 0.1; // 2 блока/с — полный размах

        for (var tick = 1; tick <= 10; tick++)
        {
            _entities.BeginTick();
            _entities.Apply(new EntitiesMoved([new EntityPosition(A, new Vector3d(0.6 * PerTick * tick, 5 - tick, 0.8 * PerTick * tick))]));
            _entities.EndTick();
        }
        Assert.Equal(1f, Get(A).CurrentWalk, 4);
        Assert.InRange(Get(A).CurrentSwing, 0.98f, 1f);
        Assert.Equal(1f - (float)PerTick / 2, Get(A).WalkAt(0.5f), 4);

        _entities.BeginTick();
        _entities.EndTick(); // встал
        var stopping = Get(A).CurrentSwing;
        Assert.Equal(Get(A).PreviousSwing * (1 - ClientEntity.SwingSmoothing), stopping, 4);
        Assert.Equal((Get(A).PreviousSwing + stopping) / 2, Get(A).SwingAt(0.5f), 4);
        Assert.Equal(1f, Get(A).CurrentWalk, 4);

        // Предмет на земле не шагает.
        _entities.Apply(Spawn(B, 0));
        _entities.BeginTick();
        _entities.Apply(Moved((B, 3)));
        _entities.EndTick();
        Assert.Equal(0f, Get(B).CurrentWalk);
    }

    /// <summary>Голова — относительно тела, от сервера; между тиками — по доле тика.</summary>
    [Fact]
    public void MobHeadTurn_FromServer_Interpolated()
    {
        _entities.Apply(new EntitySpawned(A, EntityKind.Mob, default, default, new Shared.Mobs.MobId(1), Yaw: 1, HeadTurn: 0.4f));
        Assert.Equal(0.4f, Get(A).HeadTurnAt(1));

        _entities.BeginTick();
        _entities.Apply(new EntitiesMoved([new EntityPosition(A, default, 1, -0.2f)]));
        Assert.Equal(0.1f, Get(A).HeadTurnAt(0.5f), 4);
        Assert.Equal(-0.2f, Get(A).HeadTurnAt(1), 4);

        // Тик без новостей: голова остаётся, где была, а не качается от старого поворота к новому.
        _entities.BeginTick();
        Assert.Equal(-0.2f, Get(A).HeadTurnAt(0), 4);
        Assert.Equal(-0.2f, Get(A).HeadTurnAt(0.5f), 4);
    }

    /// <summary>
    /// Моб под прицелом: луч попадает в короб тела нарисованного положения; ближайший из попавших; дальше досягаемости,
    /// мимо и предметы на земле — нет.
    /// </summary>
    [Fact]
    public void RaycastMob_NearestBodyHit_WithinReach()
    {
        var content = Shared.Content.GameContent.Load();
        var zombie = content.Mobs.Get("base:zombie");
        var sheep = content.Mobs.Get("base:sheep");
        var c = new EntityId(2, 1);
        _entities.Apply(new EntitySpawned(A, EntityKind.Mob, new Vector3d(0, 0, -3), default, zombie.Id));
        _entities.Apply(new EntitySpawned(B, EntityKind.Mob, new Vector3d(0, 0, -2), default, sheep.Id));
        _entities.Apply(new EntitySpawned(c, EntityKind.Item, new Vector3d(0, 0, -1), Stack));
        var eye = new Vector3d(0, 1, 0);
        var forward = -System.Numerics.Vector3.UnitZ;

        var hit = _entities.RaycastMob(content.Mobs, eye, forward, 5, alpha: 1);
        Assert.Equal(B, hit?.Entity);
        Assert.Equal(2 - sheep.Width / 2, hit!.Value.Distance, 5);

        // Выше овцы (1,3), но ниже зомби (1,95): луч проходит над овцой в зомби.
        var high = _entities.RaycastMob(content.Mobs, new Vector3d(0, 1.6, 0), forward, 5, alpha: 1);
        Assert.Equal((A, 3 - zombie.Width / 2), (high?.Entity, high!.Value.Distance));

        Assert.Null(_entities.RaycastMob(content.Mobs, eye, forward, 1.5f, alpha: 1));               // дальше досягаемости
        Assert.Null(_entities.RaycastMob(content.Mobs, eye, System.Numerics.Vector3.UnitZ, 5, 1));   // в другую сторону

        // Луч — по нарисованному положению: овца уходит вбок, на середине тика её короб ещё задевает луч, в конце — нет.
        _entities.BeginTick();
        _entities.Apply(new EntitiesMoved([new EntityPosition(B, new Vector3d(0.8, 0, -2))]));
        Assert.Equal(B, _entities.RaycastMob(content.Mobs, eye, forward, 5, alpha: 0.5f)?.Entity);
        Assert.Equal(A, _entities.RaycastMob(content.Mobs, eye, forward, 5, alpha: 1)?.Entity);
    }

    [Fact]
    public void MoveOfUnknown_AndOtherMessages_AreIgnored()
    {
        Assert.True(_entities.Apply(Moved((A, 1))));
        Assert.Equal(0, _entities.Count);
        Assert.False(_entities.Apply(new ChunkUnload(default)));
    }
}
