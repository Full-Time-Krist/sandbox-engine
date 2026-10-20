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

    [Fact]
    public void MoveOfUnknown_AndOtherMessages_AreIgnored()
    {
        Assert.True(_entities.Apply(Moved((A, 1))));
        Assert.Equal(0, _entities.Count);
        Assert.False(_entities.Apply(new ChunkUnload(default)));
    }
}
