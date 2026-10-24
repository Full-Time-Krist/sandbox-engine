using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Protocol;

namespace Client.Tests;

public class ClientEntitiesTests
{
    [Fact]
    public void MovementInterpolatesAndSettlesOnFollowingTick()
    {
        var entities = new ClientEntities();
        var id = new EntityId(1, 1);
        entities.Spawn(new(id, new(1000000, 3, 5), new ItemStack(new ItemId(1), 2)));
        entities.BeginTick();
        entities.Move(new([new(id, new(1000002, 3, 5))]));
        Assert.Equal(new Vector3d(1000001, 3, 5), entities.All.Single().PositionAt(0.5f));
        entities.BeginTick();
        Assert.Equal(new Vector3d(1000002, 3, 5), entities.All.Single().PositionAt(0));
    }

    [Fact]
    public void SpawnRefreshesStackAndRemovalOnlyRemovesMatchingGeneration()
    {
        var entities = new ClientEntities();
        var id = new EntityId(1, 2);
        entities.Spawn(new(id, new(2, 3, 4), new ItemStack(new ItemId(1), 2)));
        entities.Spawn(new(id, new(2, 3, 4), new ItemStack(new ItemId(1), 5)));
        entities.Remove(new(1, 1));
        Assert.Equal(5, entities.All.Single().Stack.Count);
        entities.Remove(id);
        Assert.Empty(entities.All);
    }
}
