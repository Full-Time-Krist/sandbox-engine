using Shared.Entities;

namespace Shared.Tests.Entities;

public class EntityWorldTests
{
    private record struct Position(float X);
    private record struct Velocity(float X);
    private record struct Tag;

    private readonly EntityWorld _world = new();

    private List<EntityId> Run(EntityQuery query)
    {
        var result = new List<EntityId>();
        foreach (var id in query)
            result.Add(id);
        return result;
    }

    [Fact]
    public void Create_GivesDistinctLiveIds()
    {
        var a = _world.Create();
        var b = _world.Create();

        Assert.NotEqual(a, b);
        Assert.True(_world.IsAlive(a));
        Assert.True(_world.IsAlive(b));
        Assert.False(_world.IsAlive(EntityId.None));
        Assert.Equal(2, _world.Count);
    }

    /// <summary>Главное свойство поколения: старый номер после удаления не указывает на новую сущность с тем же индексом.</summary>
    [Fact]
    public void OldId_AfterIndexReuse_DoesNotPointToNewEntity()
    {
        var old = _world.Create();
        _world.Set(old, new Position(1));
        _world.Destroy(old);
        _world.FlushDestroyed();

        var fresh = _world.Create();
        _world.Set(fresh, new Position(2));

        Assert.Equal(old.Index, fresh.Index);
        Assert.NotEqual(old, fresh);
        Assert.False(_world.IsAlive(old));
        Assert.False(_world.Has<Position>(old));
        Assert.False(_world.Destroy(old));
        Assert.Throws<InvalidOperationException>(() => _world.Get<Position>(old));
        Assert.True(_world.IsAlive(fresh));
        Assert.Equal(new Position(2), _world.Get<Position>(fresh));
    }

    /// <summary>Новая сущность на освобождённом индексе не наследует компоненты прежней.</summary>
    [Fact]
    public void ReusedIndex_StartsWithoutComponents()
    {
        var old = _world.Create();
        _world.Set(old, new Position(1));
        _world.Set(old, new Velocity(1));
        _world.Destroy(old);
        _world.FlushDestroyed();

        var fresh = _world.Create();

        Assert.Equal(old.Index, fresh.Index);
        Assert.False(_world.Has<Position>(fresh));
        Assert.False(_world.Has<Velocity>(fresh));
        Assert.Empty(Run(_world.Query<Position>()));
    }

    /// <summary>До конца тика индекс не переиспользуется: удалённая сущность мертва сразу, а номер освобождается в FlushDestroyed.</summary>
    [Fact]
    public void Destroy_IsDeferred_IndexReusedOnlyAfterFlush()
    {
        var a = _world.Create();
        _world.Set(a, new Position(1));

        Assert.True(_world.Destroy(a));
        Assert.False(_world.Destroy(a));
        Assert.False(_world.IsAlive(a));
        Assert.Equal(0, _world.Count);
        Assert.Empty(Run(_world.Query<Position>()));

        var b = _world.Create();
        Assert.NotEqual(a.Index, b.Index);

        _world.FlushDestroyed();
        var c = _world.Create();
        Assert.Equal(a.Index, c.Index);
    }

    [Fact]
    public void Components_SetGetRemove_InAnyOrder()
    {
        var e = _world.Create();
        _world.Set(e, new Velocity(3));
        _world.Set(e, new Position(1));
        _world.Get<Position>(e).X += 1;

        Assert.Equal(new Position(2), _world.Get<Position>(e));
        Assert.True(_world.Remove<Velocity>(e));
        Assert.False(_world.Remove<Velocity>(e));
        Assert.False(_world.Has<Velocity>(e));
        Assert.False(_world.Has<Tag>(e)); // хранилища этого типа ещё нет
        Assert.False(_world.Remove<Tag>(e));
        Assert.True(_world.Has<Position>(e));
    }

    [Fact]
    public void Query_SeesExactlyEntitiesWithAllComponents()
    {
        var positionOnly = _world.Create();
        _world.Set(positionOnly, new Position(0));
        var both = _world.Create();
        _world.Set(both, new Position(0));
        _world.Set(both, new Velocity(0));
        var velocityOnly = _world.Create();
        _world.Set(velocityOnly, new Velocity(0));
        var all = _world.Create();
        _world.Set(all, new Tag());
        _world.Set(all, new Velocity(0));
        _world.Set(all, new Position(0));
        var dead = _world.Create();
        _world.Set(dead, new Position(0));
        _world.Set(dead, new Velocity(0));
        _world.Destroy(dead);

        Assert.Equal([positionOnly, both, all], Run(_world.Query<Position>()).OrderBy(id => id.Index).ToList());
        Assert.Equal([both, all], Run(_world.Query<Position, Velocity>()).OrderBy(id => id.Index).ToList());
        Assert.Equal([both, all], Run(_world.Query<Velocity, Position>()).OrderBy(id => id.Index).ToList());
        Assert.Equal([all], Run(_world.Query<Position, Velocity, Tag>()));
        Assert.Equal([all], Run(_world.Query<Tag, Velocity, Position>()));
    }

    /// <summary>
    /// Обход идёт по самому маленькому хранилищу; здесь это не третье, и сущность без третьего компонента
    /// отсеивает только проверка каждого из трёх.
    /// </summary>
    [Fact]
    public void Query3_ThirdStoreLargest_StillFiltersByIt()
    {
        var all = _world.Create();
        _world.Set(all, new Position(0));
        _world.Set(all, new Velocity(0));
        _world.Set(all, new Tag());
        var noTag = _world.Create();
        _world.Set(noTag, new Position(0));
        _world.Set(noTag, new Velocity(0));
        for (var i = 0; i < 3; i++)
            _world.Set(_world.Create(), new Tag());

        Assert.Equal([all], Run(_world.Query<Position, Velocity, Tag>()));
        Assert.Equal([all], Run(_world.Query<Velocity, Tag, Position>()));
    }

    [Fact]
    public void Query_ComponentNeverAdded_IsEmpty()
    {
        var e = _world.Create();
        _world.Set(e, new Position(0));

        Assert.Empty(Run(_world.Query<Tag>()));
        Assert.Empty(Run(_world.Query<Position, Tag>()));
        Assert.Empty(Run(_world.Query<Position, Position, Tag>()));
    }

    /// <summary>
    /// Удаление посреди обхода: удалённые дальше по обходу не выдаются, остальные выдаются ровно по разу.
    /// Сущность, созданная в обходе, в него не попадает.
    /// </summary>
    [Fact]
    public void DestroyAndCreate_DuringQuery_DoNotBreakIteration()
    {
        var ids = new List<EntityId>();
        for (var i = 0; i < 10; i++)
        {
            var e = _world.Create();
            _world.Set(e, new Position(i));
            ids.Add(e);
        }

        var seen = new List<EntityId>();
        foreach (var id in _world.Query<Position>())
        {
            seen.Add(id);
            // На каждой сущности удаляем ещё не пройденную и себя через раз, создаём новую.
            foreach (var other in ids)
            {
                if (!seen.Contains(other) && _world.IsAlive(other))
                {
                    _world.Destroy(other);
                    break;
                }
            }
            if (_world.Get<Position>(id).X % 2 == 0)
                _world.Destroy(id);
            _world.Set(_world.Create(), new Position(100));
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(5, seen.Count); // каждая выданная удалила одну ещё не пройденную
        Assert.All(seen, id => Assert.Contains(id, ids));
    }

    /// <summary>Удаление компонента посреди обхода (не сущности): через swap-remove последний переезжает, но выдаётся один раз.</summary>
    [Fact]
    public void RemoveComponent_DuringQuery_NoDuplicatesNoRemovedOnes()
    {
        var ids = new List<EntityId>();
        for (var i = 0; i < 20; i++)
        {
            var e = _world.Create();
            _world.Set(e, new Position(i));
            ids.Add(e);
        }

        var seen = new List<EntityId>();
        foreach (var id in _world.Query<Position>())
        {
            seen.Add(id);
            // Убираем компонент у первой ещё не пройденной: в плотном массиве на её место встаёт последний.
            var next = ids.FirstOrDefault(e => !seen.Contains(e) && _world.Has<Position>(e));
            if (!next.IsNone)
                _world.Remove<Position>(next);
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(10, seen.Count);
        Assert.All(seen, id => Assert.True(_world.Has<Position>(id)));
    }

    [Fact]
    public void ManyEntities_CreateDestroyCycles_CountsStayConsistent()
    {
        var random = new Random(3);
        var alive = new HashSet<EntityId>();
        for (var tick = 0; tick < 200; tick++)
        {
            for (var i = 0; i < 20; i++)
            {
                var e = _world.Create();
                _world.Set(e, new Position(tick));
                Assert.True(alive.Add(e)); // номер ни разу не повторился у живой сущности
            }
            foreach (var e in alive.Where(_ => random.Next(2) == 0).ToList())
            {
                _world.Destroy(e);
                alive.Remove(e);
            }
            _world.FlushDestroyed();
        }

        Assert.Equal(alive.Count, _world.Count);
        Assert.Equal(alive.OrderBy(id => id.Index), Run(_world.Query<Position>()).OrderBy(id => id.Index));
    }
}
