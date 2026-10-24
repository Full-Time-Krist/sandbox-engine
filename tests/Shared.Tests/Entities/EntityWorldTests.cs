using Shared.Entities;

namespace Shared.Tests.Entities;

public sealed class EntityWorldTests
{
    private record struct Position(int X);
    private record struct Velocity(int X);
    private record struct Health(int Value);

    [Fact]
    public void DestroyedId_CannotAccessReusedSlot()
    {
        var world = new EntityWorld();
        var old = world.Create();
        world.Set(old, new Position(12));
        world.Destroy(old);
        Assert.True(world.IsAlive(old));
        world.FlushDestroyed();
        var next = world.Create();
        world.Set(next, new Position(34));
        Assert.Equal(old.Index, next.Index);
        Assert.NotEqual(old.Generation, next.Generation);
        Assert.False(world.IsAlive(old));
        Assert.False(world.Has<Position>(old));
        Assert.Throws<InvalidOperationException>(() => world.Set(old, new Position(99)));
        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(old));
        Assert.Equal(34, world.Get<Position>(next).X);
        Assert.False(world.IsAlive(default));
    }

    [Fact]
    public void SparseStore_RemovingMiddlePreservesMovedAndReaddedComponents()
    {
        var store = new ComponentStore<Position>();
        var first = new EntityId(0, 1);
        var middle = new EntityId(400, 1);
        var last = new EntityId(2, 1);
        store.Set(first, new Position(10));
        store.Set(middle, new Position(20));
        store.Set(last, new Position(30));
        Assert.True(store.Remove(middle));
        Assert.False(store.Has(middle));
        Assert.Equal(30, store.Get(last).X);
        store.Get(last) = new Position(31);
        store.Set(first, new Position(11));
        store.Set(middle, new Position(21));
        Assert.Equal(3, store.Count);
        Assert.Equal(11, store.Get(first).X);
        Assert.Equal(21, store.Get(middle).X);
        Assert.Equal(31, store.Get(last).X);
        Assert.False(store.Remove(new EntityId(400, 2)));
        Assert.Throws<KeyNotFoundException>(() => store.Get(new EntityId(2, 2)));
    }

    [Fact]
    public void Queries_RequireEveryComponent_AndSupportAbsentStores()
    {
        var world = new EntityWorld();
        Assert.Empty(world.Query<Position, Velocity, Health>());
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();
        var d = world.Create();
        world.Set(a, new Position(1));
        world.Set(b, new Position(2));
        world.Set(b, new Velocity(2));
        world.Set(c, new Position(3));
        world.Set(c, new Velocity(3));
        world.Set(c, new Health(20));
        world.Set(d, new Velocity(4));
        world.Set(d, new Health(20));
        Assert.Equal(new[] { a, b, c }, world.Query<Position>().OrderBy(e => e.Index));
        Assert.Equal(new[] { b, c }, world.Query<Position, Velocity>().OrderBy(e => e.Index));
        Assert.Equal(new[] { c }, world.Query<Position, Velocity, Health>());
        Assert.True(world.Remove<Velocity>(b));
        Assert.False(world.Remove<Velocity>(b));
        Assert.Equal(new[] { c }, world.Query<Position, Velocity>());
        world.Get<Position>(c) = new Position(30);
        Assert.Equal(30, world.Get<Position>(c).X);
    }

    [Fact]
    public void DestroyDuringQuery_IsDeferredAndRemovesAllComponentsOnce()
    {
        var world = new EntityWorld();
        var entities = Enumerable.Range(0, 100).Select(_ => world.Create()).ToArray();
        foreach (var entity in entities)
        {
            world.Set(entity, new Position(entity.Index));
            world.Set(entity, new Velocity(1));
        }
        var visited = new List<EntityId>();
        foreach (var entity in world.Query<Position, Velocity>())
        {
            visited.Add(entity);
            world.Destroy(entity);
            world.Destroy(entity);
        }
        Assert.Equal(entities, visited);
        Assert.Equal(100, world.Count);
        world.FlushDestroyed();
        Assert.Equal(0, world.Count);
        Assert.Empty(world.Query<Position>());
        Assert.Empty(world.Query<Velocity>());
        world.Destroy(entities[0]);
        world.FlushDestroyed();
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void TenThousandEntities_QueryAndUpdatePreserveValues()
    {
        var world = new EntityWorld();
        for (int i = 0; i < 10_000; i++)
        {
            var entity = world.Create();
            world.Set(entity, new Position(i));
            if (i % 2 == 0) world.Set(entity, new Velocity(2));
        }
        long sum = 0;
        foreach (var entity in world.Query<Position, Velocity>())
        {
            ref var position = ref world.Get<Position>(entity);
            position = new Position(position.X + world.Get<Velocity>(entity).X);
            sum += position.X;
        }
        Assert.Equal(25_005_000, sum);
        Assert.Equal(5_000, world.Query<Position, Velocity>().Count());
    }
}
